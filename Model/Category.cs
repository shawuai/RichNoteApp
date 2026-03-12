using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RichNoteApp.Model
{
    public class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int ParentId { get; set; } = 0; // 0表示主分类
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
