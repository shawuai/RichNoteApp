using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RichNoteApp.Model
{
    public class BlockEntity
    {
        public int Id { get; set; }
        public int NoteId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public int Sort { get; set; }
    }
}
