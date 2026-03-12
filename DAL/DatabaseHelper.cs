// DatabaseHelper.cs - 新增分类相关操作
using RichNoteApp.Model;
using System.Data.SQLite;
using System.Collections.Generic;
using System.IO;

namespace RichNoteApp.DAL
{
    public class DatabaseHelper
    {
        private readonly string _connectionString;

        public DatabaseHelper(string dbPath)
        {
            _connectionString = $"Data Source={dbPath};Version=3;";
            InitDatabase();
        }

        // 初始化数据库（新增分类表、笔记表增加分类ID字段）
        private void InitDatabase()
        {
            if (!File.Exists(Path.GetFileName(_connectionString)))
            {
                SQLiteConnection.CreateFile(Path.GetFileName(_connectionString));
            }

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                // 分类表
                ExecuteNonQuery(conn, @"
                    CREATE TABLE IF NOT EXISTS Categories (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        ParentId INTEGER DEFAULT 0,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )");

                // 笔记表（新增CategoryId字段）
                ExecuteNonQuery(conn, @"
                    CREATE TABLE IF NOT EXISTS Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Title TEXT NOT NULL,
                        CategoryId INTEGER DEFAULT 0,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
                    )");

                // 块表（原有）
                ExecuteNonQuery(conn, @"
                    CREATE TABLE IF NOT EXISTS Blocks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER NOT NULL,
                        Type TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        Sort INTEGER DEFAULT 0,
                        FOREIGN KEY (NoteId) REFERENCES Notes(Id) ON DELETE CASCADE
                    )");

                // 默认添加"未分类"主分类
                if (GetMainCategories().Count == 0)
                {
                    ExecuteNonQuery(conn, "INSERT INTO Categories (Name, ParentId) VALUES ('未分类', 0)");
                }
            }
        }

        #region 分类相关操作
        // 获取所有主分类（ParentId=0）
        public List<Category> GetMainCategories()
        {
            var list = new List<Category>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("SELECT Id, Name, ParentId, CreatedAt FROM Categories WHERE ParentId=0 ORDER BY Name", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(new Category
                        {
                            Id = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            ParentId = reader.GetInt32(2),
                            CreatedAt = reader.GetDateTime(3)
                        });
                    }
                }
            }
            return list;
        }

        // 根据主分类ID获取子分类
        public List<Category> GetSubCategories(int mainCategoryId)
        {
            var list = new List<Category>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("SELECT Id, Name, ParentId, CreatedAt FROM Categories WHERE ParentId=@pid ORDER BY Name", conn))
                {
                    cmd.Parameters.AddWithValue("@pid", mainCategoryId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new Category
                            {
                                Id = reader.GetInt32(0),
                                Name = reader.GetString(1),
                                ParentId = reader.GetInt32(2),
                                CreatedAt = reader.GetDateTime(3)
                            });
                        }
                    }
                }
            }
            return list;
        }

        // 新增分类
        public int AddCategory(string name, int parentId)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("INSERT INTO Categories (Name, ParentId) VALUES (@name, @pid); SELECT last_insert_rowid();", conn))
                {
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@pid", parentId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }
        #endregion

        #region 笔记相关操作（关联分类）
        // 根据分类ID获取笔记列表（带预览）
        public List<NoteListItem> GetNotesByCategoryId(int categoryId)
        {
            var list = new List<NoteListItem>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                // 关联Blocks表获取首段内容作为预览
                string sql = @"
                    SELECT n.Id, n.Title, n.UpdatedAt, n.CategoryId, 
                           COALESCE((SELECT b.Content FROM Blocks b WHERE b.NoteId = n.Id ORDER BY b.Sort LIMIT 1), '') AS PreviewContent
                    FROM Notes n WHERE n.CategoryId = @cid ORDER BY n.UpdatedAt DESC";

                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@cid", categoryId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new NoteListItem
                            {
                                Id = reader.GetInt32(0),
                                Title = reader.GetString(1),
                                UpdatedAt = reader.GetDateTime(2),
                                CategoryId = reader.GetInt32(3),
                                PreviewContent = reader.GetString(4)
                            });
                        }
                    }
                }
            }
            return list;
        }

        // 创建笔记（关联分类）
        public int CreateNote(string title, int categoryId = 0)
        {
            if (categoryId == 0)
            {
                // 默认关联"未分类"
                categoryId = GetMainCategories().FirstOrDefault()?.Id ?? 0;
            }

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("INSERT INTO Notes (Title, CategoryId, UpdatedAt) VALUES (@title, @cid, CURRENT_TIMESTAMP); SELECT last_insert_rowid();", conn))
                {
                    cmd.Parameters.AddWithValue("@title", title);
                    cmd.Parameters.AddWithValue("@cid", categoryId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        // 更新笔记分类
        public void UpdateNoteCategory(int noteId, int categoryId)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("UPDATE Notes SET CategoryId = @cid WHERE Id = @nid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", categoryId);
                    cmd.Parameters.AddWithValue("@nid", noteId);
                    cmd.ExecuteNonQuery();
                }
            }
        }
        #endregion

        #region 原有方法（保留并适配）
        public void UpdateNoteTimestamp(int noteId)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("UPDATE Notes SET UpdatedAt = CURRENT_TIMESTAMP WHERE Id = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", noteId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public List<BlockEntity> GetBlocksByNoteId(int noteId)
        {
            var list = new List<BlockEntity>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("SELECT Id, NoteId, Type, Content, Sort FROM Blocks WHERE NoteId = @id ORDER BY Sort", conn))
                {
                    cmd.Parameters.AddWithValue("@id", noteId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new BlockEntity
                            {
                                Id = reader.GetInt32(0),
                                NoteId = reader.GetInt32(1),
                                Type = reader.GetString(2),
                                Content = reader.GetString(3),
                                Sort = reader.GetInt32(4)
                            });
                        }
                    }
                }
            }
            return list;
        }

        public void SaveBlocks(int noteId, List<BlockEntity> blocks)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                // 先删除原有块
                using (var cmd = new SQLiteCommand("DELETE FROM Blocks WHERE NoteId = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", noteId);
                    cmd.ExecuteNonQuery();
                }

                // 插入新块
                for (int i = 0; i < blocks.Count; i++)
                {
                    using (var cmd = new SQLiteCommand("INSERT INTO Blocks (NoteId, Type, Content, Sort) VALUES (@nid, @type, @content, @sort)", conn))
                    {
                        cmd.Parameters.AddWithValue("@nid", noteId);
                        cmd.Parameters.AddWithValue("@type", blocks[i].Type);
                        cmd.Parameters.AddWithValue("@content", blocks[i].Content);
                        cmd.Parameters.AddWithValue("@sort", i);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }
        #endregion

        private void ExecuteNonQuery(SQLiteConnection conn, string sql)
        {
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public string GetConnectionString() => _connectionString;
    }
}