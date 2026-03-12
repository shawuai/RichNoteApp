using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using RichNoteApp.DAL;
using RichNoteApp.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;


namespace RichNoteApp
{
   

    public partial class MainWindow : Window
    {
        private DatabaseHelper _db;
        private int _currentNoteId = 0;
        private bool _isEditorReady = false;
        private bool _isSwitchingNote = false; // 防止切换时触发自动保存死循环
        private System.Threading.Timer _wordCountTimer;
        public MainWindow()
        {
            InitializeComponent();
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            try
            {
                // 1. 初始化数据库
                string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notes.db");
                _db = new DatabaseHelper(dbPath);

                // 2. 初始化 WebView2
                var envOptions = new CoreWebView2EnvironmentOptions();
                var env = await CoreWebView2Environment.CreateAsync(null, null, envOptions);
                await WebViewEditor.EnsureCoreWebView2Async(env);

                // 3. 加载本地 HTML
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "editor.html");
                if (!File.Exists(htmlPath))
                {
                    MessageBox.Show($"找不到编辑器文件：{htmlPath}");
                    return;
                }
                WebViewEditor.Source = new Uri(htmlPath);

                // 4. 订阅事件
                WebViewEditor.WebMessageReceived += OnWebMessageReceived;
                WebViewEditor.CoreWebView2.PermissionRequested += OnPermissionRequested;

                // 5. 【修复】启动定时器 - 注意这里的写法
                // 定时器回调会在后台线程运行，所以必须在回调内使用 Dispatcher
                _wordCountTimer = new System.Threading.Timer(async _ =>
                {
                    // 【关键】检查是否已就绪，并且必须切换到 UI 线程执行 WebView2 操作
                    if (_isEditorReady && _currentNoteId != 0)
                    {
                        try
                        {
                            // 使用 Dispatcher.InvokeAsync 确保在 UI 线程执行
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                try
                                {
                                    // 再次检查 CoreWebView2 是否可用，防止窗口关闭时崩溃
                                    if (WebViewEditor.CoreWebView2 != null)
                                    {
                                        await WebViewEditor.ExecuteScriptAsync("requestWordCount()");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // 静默忽略单次执行错误，避免刷屏
                                    System.Diagnostics.Debug.WriteLine($"WordCount Script Error: {ex.Message}");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Timer Dispatch Error: {ex.Message}");
                        }
                    }
                }, null, 2000, 1000); // 2秒后开始，每1秒一次

                // 6. 初始加载列表
                Task.Delay(500).ContinueWith(_ => LoadNoteList());

                TxtStatus.Text = "正在启动...";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化失败：{ex.Message}");
            }
        }

        #region 列表管理逻辑

        /// <summary>
        /// 从数据库加载笔记列表并绑定到 ListBox
        /// </summary>
        private void LoadNoteList()
        {
            if (_db == null) return;

            var notes = GetNoteListFromDb();

            Dispatcher.Invoke(() =>
            {
                lstNotes.ItemsSource = notes;

                // 【新增】更新底部笔记数量统计
                TxtNoteCount.Text = $"共 {notes.Count} 条笔记";

                if (notes.Count > 0 && _currentNoteId == 0)
                {
                    lstNotes.SelectedIndex = 0;
                }
                else if (notes.Count == 0)
                {
                    BtnNew_Click(null, null);
                }

                TxtStatus.Text = $"已加载 {notes.Count} 条笔记";
            });
        }

        // 临时辅助方法：获取列表 (实际请移到 DatabaseHelper 类中)
        private List<NoteListItem> GetNoteListFromDb()
        {
            var list = new List<NoteListItem>();
            using (var conn = new System.Data.SQLite.SQLiteConnection(_db.GetType().GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_db).ToString()))
            {
                conn.Open();
                string sql = "SELECT Id, Title, UpdatedAt FROM Notes ORDER BY UpdatedAt DESC";
                using (var cmd = new System.Data.SQLite.SQLiteCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(new NoteListItem
                        {
                            Id = reader.GetInt32(0),
                            Title = reader.GetString(1),
                            UpdatedAt = reader.GetDateTime(2)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// 列表选中项改变：加载对应笔记内容
        /// </summary>
        private async void lstNotes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingNote || lstNotes.SelectedItem == null) return;

            var selected = lstNotes.SelectedItem as NoteListItem;
            if (selected == null) return;

            // 切换前，先保存当前正在编辑的笔记（防止数据丢失）
            if (_currentNoteId != 0 && _currentNoteId != selected.Id)
            {
                await SaveCurrentNoteAsync(false); // false 表示不弹窗提示
            }

            _isSwitchingNote = true;
            _currentNoteId = selected.Id;
            TxtTitle.Text = selected.Title;
            TxtStatus.Text = $"正在加载：{selected.Title}...";

            // 加载内容
            var blocks = _db.GetBlocksByNoteId(_currentNoteId);
            string html = BlocksToHtml(blocks);
            string jsonHtml = JsonConvert.ToString(html);
            string script = $"setEditorContent({jsonHtml})";

            if (_isEditorReady)
            {
                await WebViewEditor.ExecuteScriptAsync(script);
                TxtStatus.Text = $"已加载：{selected.Title}";
            }

            _isSwitchingNote = false;
        }

        #endregion

        #region 按钮事件

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            // 先保存当前的
            if (_currentNoteId != 0)
            {
                // 异步保存但不等待，避免卡顿，或者同步保存
                // 这里简单起见，先不保存，让用户自己点保存，或者强制保存
                // 更好的体验：切换前自动保存。这里新建时，我们直接创建一个新 ID
            }

            int newId = _db.CreateNote("新笔记");
            _currentNoteId = newId;

            // 清空编辑器
            if (_isEditorReady)
            {
                WebViewEditor.ExecuteScriptAsync("setEditorContent('')");
            }
            TxtTitle.Text = "新笔记";
            TxtTitle.Focus();
            TxtTitle.SelectAll();

            // 刷新列表并选中新项
            LoadNoteList();

            // 找到刚创建的项并选中
            var newItem = lstNotes.Items.Cast<NoteListItem>().FirstOrDefault(x => x.Id == newId);
            if (newItem != null)
            {
                lstNotes.SelectedItem = newItem;
            }

            TxtStatus.Text = "已新建笔记，请输入内容...";
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNoteId == 0) return;

            var result = MessageBox.Show($"确定要删除笔记 \"{TxtTitle.Text}\" 吗？\n此操作不可恢复。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                // 调用 DAL 删除 (需要在 DatabaseHelper 中添加 DeleteNote 方法)
                // 这里用 SQL 直接演示
                using (var conn = new System.Data.SQLite.SQLiteConnection(_db.GetType().GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_db).ToString()))
                {
                    conn.Open();
                    var cmd = new System.Data.SQLite.SQLiteCommand("DELETE FROM Notes WHERE Id = @id", conn);
                    cmd.Parameters.AddWithValue("@id", _currentNoteId);
                    cmd.ExecuteNonQuery();
                }

                _currentNoteId = 0;
                TxtTitle.Text = "";
                if (_isEditorReady) WebViewEditor.ExecuteScriptAsync("setEditorContent('')");

                LoadNoteList();
                TxtStatus.Text = "笔记已删除";
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            await SaveCurrentNoteAsync(true);
        }

        /// <summary>
        /// 标题改变时，更新列表中的显示（可选：实时同步或失去焦点时同步）
        /// 这里为了性能，仅在保存时同步标题，或者你可以加个定时器
        /// </summary>
        private void TxtTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 可选：实时更新左侧列表标题
            // 但频繁刷新列表可能体验不好，建议保存时再刷新列表
        }

        #endregion

        #region 核心保存逻辑

        private async Task SaveCurrentNoteAsync(bool showNotification)
        {
            if (!_isEditorReady || _currentNoteId == 0) return;

            try
            {
                string? rawJsonResult = await WebViewEditor.ExecuteScriptAsync("getEditorContent()");
                if (string.IsNullOrEmpty(rawJsonResult)) return;

                string realHtml = JsonConvert.DeserializeObject<string>(rawJsonResult);
                var blocks = ParseHtmlToBlocks(realHtml);

                _db.UpdateNoteTimestamp(_currentNoteId);
                // 更新标题 (如果用户在 TextBox 改了标题)
                UpdateNoteTitle(_currentNoteId, TxtTitle.Text);
                _db.SaveBlocks(_currentNoteId, blocks);

                if (showNotification)
                {
                    TxtStatus.Text = $"✅ 已保存 ({DateTime.Now:HH:mm:ss})";
                    // 保存后刷新列表，以更新标题和时间
                    LoadNoteList();
                    // 重新选中当前项，防止滚动条跳动
                    var currentItem = lstNotes.Items.Cast<NoteListItem>().FirstOrDefault(x => x.Id == _currentNoteId);
                    if (currentItem != null) lstNotes.SelectedItem = currentItem;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存错误：{ex.Message}");
                if (showNotification)
                    MessageBox.Show($"保存失败：{ex.Message}");
            }
        }

        // 辅助：更新标题
        private void UpdateNoteTitle(int id, string title)
        {
            using (var conn = new System.Data.SQLite.SQLiteConnection(_db.GetType().GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_db).ToString()))
            {
                conn.Open();
                var cmd = new System.Data.SQLite.SQLiteCommand("UPDATE Notes SET Title = @title WHERE Id = @id", conn);
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        #endregion

        #region 其他事件 (权限、消息、解析) - 保持之前的逻辑不变

        private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            //if (e.PermissionType == CoreWebView2PermissionType.ClipboardRead ||
            //    e.PermissionType == CoreWebView2PermissionType.ClipboardWrite)
            //{
            //    e.State = CoreWebView2PermissionState.Allow;
            //    e.Handled = true;
            //}
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                dynamic msg = JsonConvert.DeserializeObject(json)!;
                if (msg.type == "ready")
                {
                    _isEditorReady = true;
                    Dispatcher.Invoke(() => TxtStatus.Text = "编辑器就绪");
                    // 编辑器 ready 后，如果已有选中笔记，重新加载一次确保内容显示
                    if (_currentNoteId != 0)
                    {
                        // 触发一次 SelectionChanged 逻辑或直接加载
                        var blocks = _db.GetBlocksByNoteId(_currentNoteId);
                        string html = BlocksToHtml(blocks);
                        WebViewEditor.ExecuteScriptAsync($"setEditorContent({JsonConvert.ToString(html)})");
                    }
                }
                else if (msg.type == "wordCount")
                {
                    int count = msg.count;
                    Dispatcher.Invoke(() =>
                    {
                        TxtWordCount.Text = $"{count} 字";
                    });
                }
            }
            catch { }
        }
        // 窗口关闭时清理定时器
        protected override void OnClosed(EventArgs e)
        {
            _wordCountTimer?.Dispose();
            _wordCountTimer = null;

            // 清理 WebView2 资源
            try
            {
                WebViewEditor.WebMessageReceived -= OnWebMessageReceived;
                // WebViewEditor.CoreWebView2?.Stop(); // 可选
            }
            catch { }

            base.OnClosed(e);
        }
        private List<BlockEntity> ParseHtmlToBlocks(string html)
        {
            var blocks = new List<BlockEntity>();
            if (string.IsNullOrWhiteSpace(html)) return blocks;

            // 策略：定义一个正则，匹配所有常见的“块级”HTML 标签
            // 包括：p, div, h1-h6, ul, ol, li(如果独立), blockquote, pre, table 等
            // 这里我们主要关注 p, ul, ol, h1-h6

            // 正则解释：
            // (<img[^>]*>) -> 匹配图片
            // | 或者
            // (<(p|div|h[1-6]|ul|ol|blockquote|pre)[^>]*>.*?</\2>) -> 匹配成对的块级标签
            // \2 表示引用第二个捕获组 (即标签名)，确保开始和结束标签一致 (如 <ul>...</ul>)
            string pattern = @"(<img[^>]*>)|(<(p|div|h[1-6]|ul|ol|blockquote|pre)[^>]*>.*?</\3>)";

            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                // 情况 1: 图片
                if (match.Groups[1].Success)
                {
                    string imgTag = match.Groups[1].Value;
                    var srcMatch = Regex.Match(imgTag, @"src\s*=\s*['""]([^'""]+)['""]");
                    if (srcMatch.Success && !string.IsNullOrEmpty(srcMatch.Groups[1].Value))
                    {
                        blocks.Add(new BlockEntity { Type = "Image", Content = srcMatch.Groups[1].Value });
                    }
                }
                // 情况 2: 块级元素 (p, ul, ol, h1 等)
                else if (match.Groups[2].Success)
                {
                    string fullBlock = match.Groups[2].Value; // 例如：<ul><li>1</li></ul> 或 <p>Text</p>
                    string tagName = match.Groups[3].Value.ToLower(); // 获取标签名，如 "ul", "p"

                    // 【特殊处理】：如果块级元素内部只包含一张图片 (例如 <p><img/></p> 或 <div><img/></div>)
                    // 我们优先将其识别为 Image 块，而不是包含 img 标签的 Text 块
                    string innerContent = Regex.Replace(fullBlock, @"<[^>]+>\s*</?\w+>", ""); // 粗略去除标签看是否有文字
                                                                                              // 更精确的判断：去除空白后，是否只剩下一个 img 标签
                    string noWs = Regex.Replace(fullBlock, @"\s+", "");
                    if (Regex.IsMatch(noWs, @"^<\w+[^>]*><img[^>]*></\w+>$", RegexOptions.IgnoreCase))
                    {
                        var srcMatch = Regex.Match(fullBlock, @"src\s*=\s*['""]([^'""]+)['""]");
                        if (srcMatch.Success)
                        {
                            blocks.Add(new BlockEntity { Type = "Image", Content = srcMatch.Groups[1].Value });
                            continue;
                        }
                    }

                    // 对于列表 (<ul>, <ol>) 和标题 (<h1>...)，我们直接保存完整的 HTML
                    // 这样能完美保留列表结构和样式
                    // 对于 <p> 标签，我们也保存完整 HTML 以保留 class/style (如对齐)

                    // 过滤纯空块 (可选：如果连标签里都没东西，比如 <p></p>，可以跳过)
                    if (string.IsNullOrWhiteSpace(fullBlock)) continue;

                    blocks.Add(new BlockEntity { Type = "Text", Content = fullBlock });
                }
            }

            // 调试日志
            Debug.WriteLine($"[Parse] Total Blocks: {blocks.Count}");
            foreach (var b in blocks)
            {
                string preview = b.Content.Length > 40 ? b.Content.Substring(0, 40) + "..." : b.Content;
                Debug.WriteLine($"  - [{b.Type}] {preview}");
            }

            return blocks;
        }

        private string BlocksToHtml(List<BlockEntity> blocks)
        {
            if (blocks == null || blocks.Count == 0) return "";

            var parts = new List<string>();

            foreach (var b in blocks)
            {
                if (b.Type == "Image")
                {
                    // 确保图片标签完整
                    if (!b.Content.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add($"<img src=\"{b.Content}\" style=\"max-width: 100%; display: block; margin: 5px 0;\">");
                    }
                    else
                    {
                        parts.Add(b.Content);
                    }
                }
                else if (b.Type == "Text")
                {
                    string content = b.Content.Trim();

                    if (string.IsNullOrEmpty(content)) continue;

                    // 判断是否已经是完整的块级标签 (以 < 开头)
                    if (content.StartsWith("<"))
                    {
                        // 提取标签名，判断是不是 <p>, <ul>, <ol>, <h1> 等
                        var tagMatch = Regex.Match(content, @"<(\w+)");
                        if (tagMatch.Success)
                        {
                            string tagName = tagMatch.Groups[1].Value.ToLower();
                            // 如果是已知的块级标签，直接添加，不再包裹
                            if (new[] { "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre" }.Contains(tagName))
                            {
                                parts.Add(content);
                                continue;
                            }
                        }
                    }

                    // 如果不是块级标签 (比如只是纯文本片段，或者是旧数据)，则包裹在 <p> 中
                    // 兼容旧数据：如果包含 \n，先转换
                    if (content.Contains("\n") && !content.Contains("<br>"))
                    {
                        content = System.Net.WebUtility.HtmlEncode(content).Replace("\n", "<br>");
                    }

                    parts.Add($"<p>{content}</p>");
                }
            }

            return string.Join("", parts);
        }

        #endregion
    }
}