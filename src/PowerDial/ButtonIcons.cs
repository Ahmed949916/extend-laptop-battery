using System.Drawing;
using System.Drawing.Drawing2D;

namespace PowerDial
{
    public enum ButtonIcon { None, Refresh, Undo, Export, External, Clock, Pause, Play, List, Copy, Save, Stop }

    /// <summary>Small outline icons drawn locally so buttons never depend on an installed icon font.</summary>
    internal static class ButtonIcons
    {
        internal static void Draw(Graphics g, ButtonIcon icon, RectangleF bounds, Color color)
        {
            GraphicsState state = g.Save();
            try
            {
                g.TranslateTransform(bounds.X, bounds.Y);
                g.ScaleTransform(bounds.Width / 16, bounds.Height / 16);
                using (Pen pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    switch (icon)
                    {
                        case ButtonIcon.Refresh:
                            g.DrawArc(pen, 2, 2, 12, 12, 40, 285);
                            g.DrawLines(pen, new PointF[] { new PointF(9.5f, 4.5f), new PointF(13, 4.5f), new PointF(13, 1) });
                            break;
                        case ButtonIcon.Undo:
                            g.DrawArc(pen, 3, 4, 11, 10, 195, 290);
                            g.DrawLines(pen, new PointF[] { new PointF(3, 1.5f), new PointF(3, 5.5f), new PointF(7, 5.5f) });
                            break;
                        case ButtonIcon.Export:
                            g.DrawLine(pen, 8, 1.5f, 8, 10);
                            g.DrawLines(pen, new PointF[] { new PointF(4.5f, 6.5f), new PointF(8, 10), new PointF(11.5f, 6.5f) });
                            g.DrawLines(pen, new PointF[] { new PointF(2, 10), new PointF(2, 14), new PointF(14, 14), new PointF(14, 10) });
                            break;
                        case ButtonIcon.External:
                            g.DrawLines(pen, new PointF[] { new PointF(7, 2), new PointF(2, 2), new PointF(2, 14), new PointF(14, 14), new PointF(14, 9) });
                            g.DrawLine(pen, 7, 9, 14, 2);
                            g.DrawLines(pen, new PointF[] { new PointF(9, 2), new PointF(14, 2), new PointF(14, 7) });
                            break;
                        case ButtonIcon.Clock:
                            g.DrawEllipse(pen, 1.5f, 1.5f, 13, 13);
                            g.DrawLines(pen, new PointF[] { new PointF(8, 4), new PointF(8, 8), new PointF(11, 10) });
                            break;
                        case ButtonIcon.Pause:
                            g.DrawLine(pen, 5, 3, 5, 13); g.DrawLine(pen, 11, 3, 11, 13);
                            break;
                        case ButtonIcon.Stop:
                            g.DrawRectangle(pen, 3, 3, 10, 10);
                            break;
                        case ButtonIcon.Play:
                            g.DrawPolygon(pen, new PointF[] { new PointF(4, 2), new PointF(13, 8), new PointF(4, 14) });
                            break;
                        case ButtonIcon.List:
                            for (int y = 3; y <= 13; y += 5)
                            { g.DrawLine(pen, 2, y, 3, y); g.DrawLine(pen, 6, y, 14, y); }
                            break;
                        case ButtonIcon.Copy:
                            g.DrawRectangle(pen, 5, 5, 9, 9);
                            g.DrawLines(pen, new PointF[] { new PointF(2, 11), new PointF(2, 2), new PointF(11, 2) });
                            break;
                        case ButtonIcon.Save:
                            g.DrawPolygon(pen, new PointF[] { new PointF(2, 2), new PointF(11, 2), new PointF(14, 5), new PointF(14, 14), new PointF(2, 14) });
                            g.DrawRectangle(pen, 5, 2, 5, 4); g.DrawRectangle(pen, 5, 10, 6, 4);
                            break;
                    }
                }
            }
            finally { g.Restore(state); }
        }
    }
}
