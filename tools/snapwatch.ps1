# Self-test helper: watches the test folder for "<role>-snap-<name>.req" files written by the game, captures that
# player's window (found by its "Blood Pact HOST/GUEST" title), and writes its average brightness (0-255) to
# "<role>-snap-<name>.res" next to a PNG of the capture. Started and stopped by selftest.ps1.
param([string]$Dir)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class SnapW {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  public struct RECT { public int L, T, R, B; }
  public static IntPtr Find(string part) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (sb.ToString().Contains(part)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
while ($true) {
    foreach ($req in Get-ChildItem $Dir -Filter "*-snap-*.req" -ErrorAction SilentlyContinue) {
        $base = $req.FullName.Substring(0, $req.FullName.Length - 4)
        $role = $req.Name.Split('-')[0].ToUpper()
        $value = -1
        try {
            $h = [SnapW]::Find("Blood Pact $role")
            if ($h -ne [IntPtr]::Zero) {
                $r = New-Object SnapW+RECT
                [SnapW]::GetWindowRect($h, [ref]$r) | Out-Null
                $w = $r.R - $r.L; $hh = $r.B - $r.T
                $bmp = New-Object System.Drawing.Bitmap $w, $hh
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $dc = $g.GetHdc(); [SnapW]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
                $sum = 0; $n = 0
                for ($y = [int]($hh * 0.1); $y -lt $hh * 0.9; $y += 12) { for ($x = [int]($w * 0.1); $x -lt $w * 0.9; $x += 12) { $c = $bmp.GetPixel($x, $y); $sum += ($c.R + $c.G + $c.B) / 3; $n++ } }
                if ($n -gt 0) { $value = [int]($sum / $n) }
                $bmp.Save("$base.png"); $bmp.Dispose()
            }
        } catch { }
        Set-Content -Path "$base.res" -Value $value
        Remove-Item $req.FullName -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 300
}
