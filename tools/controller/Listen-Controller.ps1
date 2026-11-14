<#
 Read-only physical-controller listener (Windows PowerShell 5.1). Logs button/axis/switch changes from
 XInput slots 0-3 and Windows.Gaming.Input RawGameControllers (DInput pads such as 8BitDo, Stadia, Joy-Con).
 Detects idle drift (axis away from rest while untouched) and phantom presses, hot-plug and unplug.
 powershell.exe -NoProfile -File tools/controller/Listen-Controller.ps1 -Seconds 60 -Out result.txt
#>
param([int]$Seconds = 60, [string]$Out = "")
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class XI { [StructLayout(LayoutKind.Sequential)] public struct S{public uint P;public ushort B;public byte LT,RT;public short LX,LY,RX,RY;}
 [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint i,out S s); }
'@
[void][Windows.Gaming.Input.RawGameController,Windows.Gaming.Input,ContentType=WindowsRuntime]
$log = New-Object System.Collections.Generic.List[string]
function L($m){ $t=(Get-Date).ToString('HH:mm:ss.fff'); $x="$t $m"; $log.Add($x); Write-Host $x }
$prevX=@{}; $prevR=@{}; $seen=@{}
$end=(Get-Date).AddSeconds($Seconds)
L "listening $Seconds s (PHYSICAL input, read-only)"
while((Get-Date) -lt $end){
 for($i=0;$i -lt 4;$i++){ $s=New-Object XI+S; $r=[XI]::XInputGetState($i,[ref]$s)
  if($r -eq 0){ if(-not $prevX.ContainsKey($i)){ L "XInput slot $i CONNECTED" ; $prevX[$i]=$null }
   $k="{0:X4}|{1}|{2}|{3}|{4}|{5}|{6}" -f $s.B,$s.LT,$s.RT,([int]($s.LX/4096)),([int]($s.LY/4096)),([int]($s.RX/4096)),([int]($s.RY/4096))
   if($prevX[$i] -ne $k){ L ("XInput[$i] buttons=0x{0:X4} LT={1} RT={2} L=({3},{4}) R=({5},{6})" -f $s.B,$s.LT,$s.RT,$s.LX,$s.LY,$s.RX,$s.RY); $prevX[$i]=$k } }
  elseif($prevX.ContainsKey($i)){ L "XInput slot $i DISCONNECTED"; $prevX.Remove($i) } }
 $cur=@{}; foreach($c in [Windows.Gaming.Input.RawGameController]::RawGameControllers){
  $id=$c.NonRoamableId; $cur[$id]=1
  if(-not $seen.ContainsKey($id)){ L ("RAW CONNECTED '{0}' VID=0x{1:X4} PID=0x{2:X4} buttons={3} axes={4} wireless={5}" -f $c.DisplayName,$c.HardwareVendorId,$c.HardwareProductId,$c.ButtonCount,$c.AxisCount,$c.IsWireless); $seen[$id]=$c.DisplayName }
  $b=New-Object 'bool[]' $c.ButtonCount; $sw=New-Object 'Windows.Gaming.Input.GameControllerSwitchPosition[]' $c.SwitchCount; $ax=New-Object 'double[]' $c.AxisCount
  [void]$c.GetCurrentReading($b,$sw,$ax)
  $pressed=@(0..($b.Length-1) | ? { $b[$_] }) -join ','; $axs=($ax | % { [Math]::Round($_,2) }) -join ','
  $k="B[$pressed] SW[$($sw -join ',')] AX[$axs]"
  if($prevR[$id] -ne $k){ L "RAW '$($c.DisplayName)' $k"; $prevR[$id]=$k } }
 foreach($id in @($seen.Keys)){ if(-not $cur.ContainsKey($id)){ L "RAW DISCONNECTED '$($seen[$id])'"; $seen.Remove($id); $prevR.Remove($id) } }
 Start-Sleep -Milliseconds 8 }
L "done"
if($Out){ $log | Set-Content -Path $Out -Encoding UTF8 }
