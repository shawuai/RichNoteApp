using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RichNoteApp.Model
{
    public class BlockEntity
    {
        public string Type { get; set; } // "Text" 或 "Image"

        // 之前：Text 类型存纯文本，\n 代表换行
        // 现在：Text 类型存 HTML 片段 (如 "Hello <span style='color:red'>World</span>"), <br> 代表换行
        public string Content { get; set; }
    }
}
