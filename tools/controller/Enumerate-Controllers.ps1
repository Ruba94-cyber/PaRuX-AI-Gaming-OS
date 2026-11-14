<#
 Read-only controller enumeration (XInput slots, Windows.Gaming.Input, raw HID/RawInput).
 Run with Windows PowerShell 5.1 (WinRT projection). Does not write, pair, or change drivers/settings.
 powershell.exe -NoProfile -File tools/controller/Enumerate-Controllers.ps1
#>
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class CtlProbe {
  [StructLayout(LayoutKind.Sequential)] public struct XGamepad{public ushort Buttons;public byte LT,RT;public short LX,LY,RX,RY;}
  [StructLayout(LayoutKind.Sequential)] public struct XState{public uint Packet;public XGamepad Pad;}
  [StructLayout(LayoutKind.Sequential)] public struct XCaps{public byte Type,SubType;public ushort Flags;public XGamepad Pad;public ushort VL,VR;}
  [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint i, out XState s);
  [DllImport("xinput1_4.dll")] static extern uint XInputGetCapabilities(uint i,uint f,out XCaps c);
  public static string XInput(){var sb=new StringBuilder();
    for(uint i=0;i<4;i++){XState s; uint r=XInputGetState(i,out s);
      if(r!=0){sb.AppendLine("  slot "+i+": empty (code "+r+")");continue;}
      XCaps c; XInputGetCapabilities(i,0,out c);
      sb.AppendLine("  slot "+i+": CONNECTED type="+c.Type+" subtype="+c.SubType+" flags=0x"+c.Flags.ToString("X")+" packet="+s.Packet+" buttons=0x"+s.Pad.Buttons.ToString("X4")+" LT="+s.Pad.LT+" RT="+s.Pad.RT+" L=("+s.Pad.LX+","+s.Pad.LY+") R=("+s.Pad.RX+","+s.Pad.RY+")");}
    return sb.ToString();}
  [StructLayout(LayoutKind.Sequential)] struct RIDL{public IntPtr h;public uint t;}
  [StructLayout(LayoutKind.Sequential)] struct RIDI_HID{public uint Vid,Pid,Ver;public ushort UsagePage,Usage;}
  [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([In,Out]RIDL[] l, ref uint n, uint sz);
  [DllImport("user32.dll",EntryPoint="GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfoW(IntPtr h,uint cmd,IntPtr data,ref uint sz);
  public static List<string> Raw(){var res=new List<string>();uint n=0;uint sz=(uint)Marshal.SizeOf(typeof(RIDL));
    GetRawInputDeviceList(null,ref n,sz);var l=new RIDL[n];GetRawInputDeviceList(l,ref n,sz);
    foreach(var d in l){ if(d.t!=2)continue; uint len=0; GetRawInputDeviceInfoW(d.h,0x20000007,IntPtr.Zero,ref len);
      IntPtr b=Marshal.AllocHGlobal((int)len*2+2); GetRawInputDeviceInfoW(d.h,0x20000007,b,ref len); string name=Marshal.PtrToStringUni(b); Marshal.FreeHGlobal(b);
      uint isz=(uint)Marshal.SizeOf(typeof(RIDI_HID))+8; IntPtr p=Marshal.AllocHGlobal(32); uint sz2=32;
      // RID_DEVICE_INFO: cbSize, dwType, union
      Marshal.WriteInt32(p,0,32); GetRawInputDeviceInfoW(d.h,0x2000000b,p,ref sz2);
      uint vid=(uint)Marshal.ReadInt32(p,8),pid=(uint)Marshal.ReadInt32(p,12);ushort up=(ushort)Marshal.ReadInt16(p,20),us=(ushort)Marshal.ReadInt16(p,22);Marshal.FreeHGlobal(p);
      if(up==1&&(us==4||us==5)||up==0xFF00||up==1&&us==8||name.Contains("IG_")) res.Add("  VID_"+vid.ToString("X4")+" PID_"+pid.ToString("X4")+" usagePage=0x"+up.ToString("X")+" usage=0x"+us.ToString("X")+" "+name);}
    return res;}
}
'@
"== XInput slots (xinput1_4, read-only GetState) =="; [CtlProbe]::XInput()
"== Raw HID devices: gamepad/joystick usages and XInput-interface (IG_) nodes =="; [CtlProbe]::Raw()
"== Windows.Gaming.Input =="
try {
 [void][Windows.Gaming.Input.Gamepad,Windows.Gaming.Input,ContentType=WindowsRuntime]
 [void][Windows.Gaming.Input.RawGameController,Windows.Gaming.Input,ContentType=WindowsRuntime]
 $g=[Windows.Gaming.Input.Gamepad]::Gamepads; "  Gamepad class (XInput-style) count: $($g.Count)"
 $r=[Windows.Gaming.Input.RawGameController]::RawGameControllers; "  RawGameController count: $($r.Count)"
 foreach($c in $r){ "  - '$($c.DisplayName)' HardwareVID=0x{0:X4} PID=0x{1:X4} buttons=$($c.ButtonCount) axes=$($c.AxisCount) switches=$($c.SwitchCount) wireless=$($c.IsWireless) haptics=$($c.ForceFeedbackMotors.Count)" -f $c.HardwareVendorId,$c.HardwareProductId }
} catch { "  WGI unavailable from this host: $($_.Exception.Message)" }
