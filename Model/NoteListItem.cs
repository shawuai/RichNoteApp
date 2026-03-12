using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RichNoteApp.Model
{
    // 用于左侧列表绑定的简单模型
    public class NoteListItem
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
