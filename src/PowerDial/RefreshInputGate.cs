using System;
using System.Windows.Forms;

namespace PowerDial
{
    /// <summary>Block re-entrant refresh input without changing controls' enabled appearance.</summary>
    internal sealed class RefreshInputGate : IMessageFilter, IDisposable
    {
        readonly Form _owner;
        internal RefreshInputGate(Form owner) { _owner = owner; Application.AddMessageFilter(this); }

        internal static bool IsInteraction(int message, long keys)
        {
            return (message >= 0x100 && message <= 0x109) || // keyboard, characters, shortcuts
                (message >= 0x201 && message <= 0x20E) ||  // buttons and wheels
                (message >= 0xA1 && message <= 0xA9) ||    // title-bar buttons
                (message == 0x200 && (keys & 0x73) != 0);  // dragging, but allow normal hover
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (!IsInteraction(message.Msg, message.WParam.ToInt64())) return false;
            Control target = Control.FromChildHandle(message.HWnd);
            return target != null && (target.FindForm() == _owner || target is ToolStripDropDown);
        }

        public void Dispose() { Application.RemoveMessageFilter(this); }
    }
}
