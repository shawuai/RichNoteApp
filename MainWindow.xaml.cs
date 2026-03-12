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
using System.Windows.Input;

namespace RichNoteApp
{
    public partial class MainWindow : Window
    {
        private DatabaseHelper _db;
        private int _currentCategoryId = 0; // 当前选中的分类ID
        private int _currentNoteId = 0; // 当前编辑的笔记ID
        private bool _isEditorReady = false;
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

                // 2. 加载分类列表
                LoadMainCategories();

                // 3. 初始化WebView2编辑器
                var env = await CoreWebView2Environment.CreateAsync(null, null, new CoreWebView2EnvironmentOptions());
                await WebViewEditor.EnsureCoreWebView2Async(env);

                // 4. 加载编辑器HTML
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "editor.html");
                if (File.Exists(htmlPath))
                {
                    WebViewEditor.Source = new Uri(htmlPath);
                }
                else
                {
                    MessageBox.Show($"编辑器文件不存在：{htmlPath}");
                }

                // 5. 绑定WebView事件
                WebViewEditor.WebMessageReceived += OnWebMessageReceived;
                WebViewEditor.CoreWebView2.PermissionRequested += (s, e) =>
                {
                    // 兼容旧版 WebView2，直接允许权限
                    e.State = CoreWebView2PermissionState.Allow;
                    e.Handled = true;
                };

                // 6. 初始化字数统计定时器
                _wordCountTimer = new System.Threading.Timer(async _ =>
                {
                    if (_isEditorReady && _currentNoteId != 0)
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            try
                            {
                                if (WebViewEditor.CoreWebView2 != null)
                                {
                                    await WebViewEditor.ExecuteScriptAsync("requestWordCount()");
                                }
                            }
                            catch { }
                        });
                    }
                }, null, 2000, 1000);

                TxtEditorStatus.Text = "应用已就绪";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化失败：{ex.Message}");
            }
        }

        #region 分类管理逻辑
        // 加载主分类
        private void LoadMainCategories()
        {
            var mainCats = _db.GetMainCategories();
            Dispatcher.Invoke(() =>
            {
                LstMainCategories.ItemsSource = mainCats;
                TxtCategoryCount.Text = $"分类：{mainCats.Count}";
                if (mainCats.Count > 0)
                {
                    LstMainCategories.SelectedIndex = 0;
                }
            });
        }

        // 加载子分类
        private void LoadSubCategories(int mainCategoryId)
        {
            var subCats = _db.GetSubCategories(mainCategoryId);
            Dispatcher.Invoke(() =>
            {
                LstSubCategories.ItemsSource = subCats;
                // 默认选中第一个子分类（如果有）
                if (subCats.Count > 0)
                {
                    LstSubCategories.SelectedIndex = 0;
                }
                else
                {
                    // 无子分类时，加载主分类下的笔记
                    _currentCategoryId = mainCategoryId;
                    LoadNotesByCategoryId(mainCategoryId);
                }
            });
        }

        // 新增主分类
        private void BtnAddMainCategory_Click(object sender, RoutedEventArgs e)
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox("请输入主分类名称：", "新增主分类", "未命名分类");
            if (!string.IsNullOrWhiteSpace(input))
            {
                _db.AddCategory(input, 0);
                LoadMainCategories();
            }
        }

        // 新增子分类
        private void BtnAddSubCategory_Click(object sender, RoutedEventArgs e)
        {
            if (LstMainCategories.SelectedItem is not Category mainCat)
            {
                MessageBox.Show("请先选择一个主分类");
                return;
            }

            var input = Microsoft.VisualBasic.Interaction.InputBox("请输入子分类名称：", "新增子分类", "未命名子分类");
            if (!string.IsNullOrWhiteSpace(input))
            {
                _db.AddCategory(input, mainCat.Id);
                LoadSubCategories(mainCat.Id);
            }
        }

        // 主分类选中变更
        private void LstMainCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstMainCategories.SelectedItem is Category mainCat)
            {
                LoadSubCategories(mainCat.Id);
            }
        }

        // 子分类选中变更
        private void LstSubCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstSubCategories.SelectedItem is Category subCat)
            {
                _currentCategoryId = subCat.Id;
                LoadNotesByCategoryId(subCat.Id);
            }
        }
        #endregion

        #region 笔记列表逻辑
        // 加载分类下的笔记
        private void LoadNotesByCategoryId(int categoryId)
        {
            var notes = _db.GetNotesByCategoryId(categoryId);
            Dispatcher.Invoke(() =>
            {
                LstNoteThumbnails.ItemsSource = notes;
                TxtNoteCount.Text = $"笔记：{notes.Count}";
                // 清空编辑区
                _currentNoteId = 0;
                GridEditor.Visibility = Visibility.Collapsed;
            });
        }

        // 新建笔记
        private void BtnNewNote_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCategoryId == 0)
            {
                MessageBox.Show("请先选择一个分类");
                return;
            }

            // 创建新笔记
            int newNoteId = _db.CreateNote("新笔记", _currentCategoryId);
            _currentNoteId = newNoteId;
            _currentCategoryId = _currentCategoryId;

            // 刷新笔记列表
            LoadNotesByCategoryId(_currentCategoryId);

            // 打开编辑区
            Dispatcher.Invoke(() =>
            {
                TxtNoteTitle.Text = "新笔记";
                GridEditor.Visibility = Visibility.Visible;
                if (_isEditorReady)
                {
                    WebViewEditor.ExecuteScriptAsync("setEditorContent('')");
                }
                TxtNoteTitle.Focus();
                TxtNoteTitle.SelectAll();
            });
        }

        // 删除笔记
        private void BtnDeleteNote_Click(object sender, RoutedEventArgs e)
        {
            if (LstNoteThumbnails.SelectedItem is not NoteListItem selectedNote)
            {
                MessageBox.Show("请先选择要删除的笔记");
                return;
            }

            if (MessageBox.Show($"确定删除笔记「{selectedNote.Title}」？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                // 删除笔记（级联删除Blocks）
                using (var conn = new System.Data.SQLite.SQLiteConnection(_db.GetConnectionString()))
                {
                    conn.Open();
                    using (var cmd = new System.Data.SQLite.SQLiteCommand("DELETE FROM Notes WHERE Id = @id", conn))
                    {
                        cmd.Parameters.AddWithValue("@id", selectedNote.Id);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 刷新列表
                LoadNotesByCategoryId(_currentCategoryId);
                // 关闭编辑区
                GridEditor.Visibility = Visibility.Collapsed;
                _currentNoteId = 0;
            }
        }

        // 点击笔记缩略项打开编辑区
        private void LstNoteThumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstNoteThumbnails.SelectedItem is NoteListItem selectedNote)
            {
                _currentNoteId = selectedNote.Id;
                Dispatcher.Invoke(async () =>
                {
                    // 加载笔记内容
                    TxtNoteTitle.Text = selectedNote.Title;
                    GridEditor.Visibility = Visibility.Visible;

                    if (_isEditorReady)
                    {
                        var blocks = _db.GetBlocksByNoteId(selectedNote.Id);
                        string html = BlocksToHtml(blocks);
                        await WebViewEditor.ExecuteScriptAsync($"setEditorContent({JsonConvert.ToString(html)})");
                    }
                });
            }
        }
        #endregion

        #region 编辑区逻辑
        // 保存笔记
        private async void BtnSaveNote_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNoteId == 0 || !_isEditorReady) return;

            try
            {
                // 获取编辑器内容
                string rawContent = await WebViewEditor.ExecuteScriptAsync("getEditorContent()");
                string htmlContent = JsonConvert.DeserializeObject<string>(rawContent);
                var blocks = ParseHtmlToBlocks(htmlContent);

                // 更新笔记标题和时间
                using (var conn = new System.Data.SQLite.SQLiteConnection(_db.GetConnectionString()))
                {
                    conn.Open();
                    using (var cmd = new System.Data.SQLite.SQLiteCommand(
                        "UPDATE Notes SET Title = @title, UpdatedAt = CURRENT_TIMESTAMP WHERE Id = @id", conn))
                    {
                        cmd.Parameters.AddWithValue("@title", TxtNoteTitle.Text.Trim());
                        cmd.Parameters.AddWithValue("@id", _currentNoteId);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 保存内容块
                _db.SaveBlocks(_currentNoteId, blocks);

                // 刷新笔记列表
                LoadNotesByCategoryId(_currentCategoryId);
                TxtEditorStatus.Text = $"✅ 保存成功 {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}");
                TxtEditorStatus.Text = "❌ 保存失败";
            }
        }

        // 关闭编辑区
        private void BtnCloseEditor_Click(object sender, RoutedEventArgs e)
        {
            GridEditor.Visibility = Visibility.Collapsed;
            LstNoteThumbnails.SelectedItem = null;
            _currentNoteId = 0;
        }

        // WebView消息接收（编辑器就绪/字数统计）
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                dynamic msg = JsonConvert.DeserializeObject(e.TryGetWebMessageAsString())!;
                if (msg.type == "ready")
                {
                    _isEditorReady = true;
                    Dispatcher.Invoke(() => TxtEditorStatus.Text = "编辑器就绪");
                }
                else if (msg.type == "wordCount")
                {
                    int count = msg.count;
                    Dispatcher.Invoke(() => TxtWordCount.Text = $"{count} 字");
                }
            }
            catch { }
        }
        #endregion

        #region HTML与Block转换（原有逻辑保留）
        private List<BlockEntity> ParseHtmlToBlocks(string html)
        {
            var blocks = new List<BlockEntity>();
            if (string.IsNullOrWhiteSpace(html)) return blocks;

            string pattern = @"(<img[^>]*>)|(<(p|div|h[1-6]|ul|ol|blockquote|pre)[^>]*>.*?</\3>)";
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                // 图片块
                if (match.Groups[1].Success)
                {
                    string imgTag = match.Groups[1].Value;
                    var srcMatch = Regex.Match(imgTag, @"src\s*=\s*['""]([^'""]+)['""]");
                    if (srcMatch.Success)
                    {
                        blocks.Add(new BlockEntity { Type = "Image", Content = srcMatch.Groups[1].Value });
                    }
                }
                // 文本块
                else if (match.Groups[2].Success)
                {
                    string fullBlock = match.Groups[2].Value;
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

                    if (!string.IsNullOrWhiteSpace(fullBlock))
                    {
                        blocks.Add(new BlockEntity { Type = "Text", Content = fullBlock });
                    }
                }
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

                    if (content.StartsWith("<"))
                    {
                        var tagMatch = Regex.Match(content, @"<(\w+)");
                        if (tagMatch.Success)
                        {
                            string tagName = tagMatch.Groups[1].Value.ToLower();
                            if (new[] { "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "blockquote", "pre" }.Contains(tagName))
                            {
                                parts.Add(content);
                                continue;
                            }
                        }
                    }

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

        // 窗口关闭清理资源
        protected override void OnClosed(EventArgs e)
        {
            _wordCountTimer?.Dispose();
            WebViewEditor?.Dispose();
            base.OnClosed(e);
        }
    }
}