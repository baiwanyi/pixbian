$path = 'C:\Users\Baiwanyi\.nuget\packages\microsoft.windowsappsdk.winui\2.3.6\lib\native\Microsoft.UI\Themes\generic.xaml'
$txt = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
$marker = 'TargetType="MenuFlyoutItem"'
$idx = $txt.IndexOf($marker)
if ($idx -lt 0) { $idx = $txt.IndexOf('MenuFlyoutItem') }
$start = [Math]::Max(0, $idx - 100)
Write-Output "idx=$idx"
Write-Output $txt.Substring($start, [Math]::Min(5000, $txt.Length - $start))
