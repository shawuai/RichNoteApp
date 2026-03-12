using RichNoteApp.Model;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;

namespace RichNoteApp.DAL
{
    /// <summary>
    /// 数据库帮助类：负责 SQLite 连接、初始化及基础 CRUD
    /// </summary>
    public class DatabaseHelper
    {
        private readonly string _connectionString;

        public DatabaseHelper(string dbPath)
        {
            _connectionString = $"Data Source={dbPath};Version=3;";
            InitializeDatabase();
        }

        /// <summary>
        /// 初始化数据库表结构
        /// </summary>
        private void InitializeDatabase()
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                string sql = @"
                CREATE TABLE IF NOT EXISTS Notes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS Blocks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NoteId INTEGER NOT NULL,
                    BlockType TEXT NOT NULL, -- 'Text' or 'Image'
                    Content TEXT NOT NULL,   -- 文本内容 或 图片 Base64
                    SortOrder INTEGER NOT NULL,
                    FOREIGN KEY(NoteId) REFERENCES Notes(Id) ON DELETE CASCADE
                );
                ";
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        #region Note Operations

        public int CreateNote(string title)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                string sql = "INSERT INTO Notes (Title) VALUES (@title); SELECT last_insert_rowid();";
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@title", title);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public void UpdateNoteTimestamp(int noteId)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                string sql = "UPDATE Notes SET UpdatedAt = CURRENT_TIMESTAMP WHERE Id = @id";
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", noteId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        #endregion

        #region Block Operations

        /// <summary>
        /// 保存一组块到数据库（先删除旧的，再插入新的）
        /// </summary>
        public void SaveBlocks(int noteId, List<BlockEntity> blocks)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var trans = conn.BeginTransaction())
                {
                    try
                    {
                        // 1. 删除旧块
                        string deleteSql = "DELETE FROM Blocks WHERE NoteId = @noteId";
                        using (var cmd = new SQLiteCommand(deleteSql, conn, trans))
                        {
                            cmd.Parameters.AddWithValue("@noteId", noteId);
                            cmd.ExecuteNonQuery();
                        }

                        // 2. 插入新块
                        string insertSql = "INSERT INTO Blocks (NoteId, BlockType, Content, SortOrder) VALUES (@noteId, @type, @content, @order)";
                        using (var cmd = new SQLiteCommand(insertSql, conn, trans))
                        {
                            cmd.Parameters.AddWithValue("@noteId", noteId);
                            cmd.Parameters.Add("@type", DbType.String);
                            cmd.Parameters.Add("@content", DbType.String);
                            cmd.Parameters.Add("@order", DbType.Int32);

                            for (int i = 0; i < blocks.Count; i++)
                            {
                                cmd.Parameters["@type"].Value = blocks[i].Type;
                                cmd.Parameters["@content"].Value = blocks[i].Content;
                                cmd.Parameters["@order"].Value = i;
                                cmd.ExecuteNonQuery();
                            }
                        }
                        trans.Commit();
                    }
                    catch
                    {
                        trans.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// 获取某个笔记的所有块，并按顺序排列
        /// </summary>
        public List<BlockEntity> GetBlocksByNoteId(int noteId)
        {
            var blocks = new List<BlockEntity>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                string sql = "SELECT BlockType, Content FROM Blocks WHERE NoteId = @noteId ORDER BY SortOrder ASC";
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@noteId", noteId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            blocks.Add(new BlockEntity
                            {
                                Type = reader["BlockType"].ToString(),
                                Content = reader["Content"].ToString()
                            });
                        }
                    }
                }
            }
            return blocks;
        }

        #endregion
    }

    
}