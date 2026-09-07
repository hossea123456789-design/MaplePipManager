using System.Runtime.InteropServices;

namespace CropPipViewer;

internal static class FilterKeysService
{
    private const uint SPI_GETFILTERKEYS = 0x0032;
    private const uint SPI_SETFILTERKEYS = 0x0033;
    // Windows 프로필에 매번 저장하면 게임 중 stutter가 생길 수 있으므로,
    // 런타임 적용은 브로드캐스트만 사용한다.
    private const uint SPIF_SENDCHANGE = 0x0002;

    private const uint FKF_FILTERKEYSON = 0x00000001;
    private const uint FKF_AVAILABLE = 0x00000002;
    private const uint FKF_HOTKEYACTIVE = 0x00000004;
    private const uint FKF_CONFIRMHOTKEY = 0x00000008;
    private const uint FKF_HOTKEYSOUND = 0x00000010;
    private const uint FKF_INDICATOR = 0x00000020;
    private const uint FKF_CLICKON = 0x00000040;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILTERKEYS
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;
        public uint iDelayMSec;
        public uint iRepeatMSec;
        public uint iBounceMSec;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref FILTERKEYS pvParam, uint fWinIni);

    public static void Apply(int acceptDelayMs, int repeatDelayMs, int repeatRateMs)
    {
        var keys = GetCurrent();
        keys.dwFlags |= FKF_FILTERKEYSON | FKF_AVAILABLE;

        // 외부 단축키/확인/소리 팝업은 게임 중 방해가 되기 쉬워 기본 비활성화한다.
        keys.dwFlags &= ~FKF_HOTKEYACTIVE;
        keys.dwFlags &= ~FKF_CONFIRMHOTKEY;
        keys.dwFlags &= ~FKF_HOTKEYSOUND;
        keys.dwFlags &= ~FKF_CLICKON;
        keys.dwFlags |= FKF_INDICATOR;

        keys.iWaitMSec = (uint)Math.Clamp(acceptDelayMs, 0, 5000);
        keys.iDelayMSec = (uint)Math.Clamp(repeatDelayMs, 0, 5000);
        keys.iRepeatMSec = (uint)Math.Clamp(repeatRateMs, 1, 5000);
        keys.iBounceMSec = 0;
        Set(keys);
    }

    public static void TurnOff()
    {
        var keys = GetCurrent();
        keys.dwFlags &= ~FKF_FILTERKEYSON;
        Set(keys);
    }

    public static bool IsEnabled()
    {
        var keys = GetCurrent();
        return (keys.dwFlags & FKF_FILTERKEYSON) != 0;
    }

    private static FILTERKEYS GetCurrent()
    {
        var keys = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>() };
        if (!SystemParametersInfo(SPI_GETFILTERKEYS, keys.cbSize, ref keys, 0))
        {
            throw new InvalidOperationException($"SPI_GETFILTERKEYS 실패. Win32Error={Marshal.GetLastWin32Error()}");
        }
        return keys;
    }

    private static void Set(FILTERKEYS keys)
    {
        keys.cbSize = (uint)Marshal.SizeOf<FILTERKEYS>();
        if (!SystemParametersInfo(SPI_SETFILTERKEYS, keys.cbSize, ref keys, SPIF_SENDCHANGE))
        {
            throw new InvalidOperationException($"SPI_SETFILTERKEYS 실패. Win32Error={Marshal.GetLastWin32Error()}");
        }
    }
}
