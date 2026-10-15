using System.Runtime.InteropServices;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace CombatLogViewer;

/// <summary>
/// Drags a file or folder exactly the way File Explorer does: the shell builds the data object for
/// the item and runs the drag, which gives the folder thumbnail and the "+ Copy" label that drop
/// targets (browsers, Explorer) expect. WPF's own DoDragDrop shows bare cursors instead.
/// </summary>
static class ShellDrag
{
    const uint DROPEFFECT_COPY = 1, DROPEFFECT_LINK = 4;

    /// <summary>True while one of our own drags is in progress.</summary>
    public static bool Active { get; private set; }

    /// <summary>Runs a modal shell drag of <paramref name="path"/>; false if the shell couldn't start it.</summary>
    public static bool DoDragDrop(string path)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            var bhidDataObject = new Guid("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
            var data = (ComIDataObject)item.BindToHandler(IntPtr.Zero, bhidDataObject, typeof(ComIDataObject).GUID);

            // Let drop targets put their own text under the cursor ("Copy to Downloads"), as in Explorer.
            try
            {
                var helper = (IDragSourceHelper2)new DragDropHelper();
                helper.SetFlags(1 /* DSH_ALLOWDROPDESCRIPTIONTEXT */);
                var pt = new POINT();
                helper.InitializeFromWindow(IntPtr.Zero, ref pt, data); // no window: the shell draws the item's image
            }
            catch (COMException) { /* drag still works, just without the image */ }

            // Copy (and link) only: never Move, so dropping onto a folder can't take the run out of the logs.
            Active = true;
            try { SHDoDragDrop(IntPtr.Zero, data, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_LINK, out _); }
            finally { Active = false; }
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the shell drag image (folder thumbnail) drawn while a drag is over one of our own windows.
    /// Drop targets must report the drag to the shell helper, or the image disappears over them.
    /// </summary>
    public static class TargetHelper
    {
        static IDropTargetHelper? helper;

        static IDropTargetHelper? Helper
        {
            get
            {
                try { return helper ??= (IDropTargetHelper)new DragDropHelper(); }
                catch (COMException) { return null; }
            }
        }

        static POINT ScreenPoint(System.Windows.Point screen) => new() { X = (int)screen.X, Y = (int)screen.Y };

        public static void Enter(IntPtr hwnd, object data, System.Windows.Point screen, int effect)
        {
            if (data is not ComIDataObject com) return;
            var pt = ScreenPoint(screen);
            try { Helper?.DragEnter(hwnd, com, ref pt, effect); } catch (COMException) { }
        }

        public static void Over(System.Windows.Point screen, int effect)
        {
            var pt = ScreenPoint(screen);
            try { Helper?.DragOver(ref pt, effect); } catch (COMException) { }
        }

        public static void Leave()
        {
            try { Helper?.DragLeave(); } catch (COMException) { }
        }

        public static void Drop(object data, System.Windows.Point screen, int effect)
        {
            if (data is not ComIDataObject com) return;
            var pt = ScreenPoint(screen);
            try { Helper?.Drop(com, ref pt, effect); } catch (COMException) { }
        }
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, ComIDataObject data, ref POINT pt, int effect);
        void DragLeave();
        void DragOver(ref POINT pt, int effect);
        void Drop(ComIDataObject data, ref POINT pt, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHDoDragDrop(IntPtr hwnd, ComIDataObject data, IntPtr dropSource, uint okEffects, out uint effect);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object BindToHandler(IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);
        // Remaining IShellItem methods are not used.
    }

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    class DragDropHelper { }

    [ComImport, Guid("83E07D0D-0C5F-4163-BF1A-60B274051E40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDragSourceHelper2
    {
        void InitializeFromBitmap(IntPtr dragImage, ComIDataObject data);
        void InitializeFromWindow(IntPtr hwnd, ref POINT pt, ComIDataObject data);
        void SetFlags(int flags);
    }
}
