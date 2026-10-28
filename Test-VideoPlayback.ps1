<#
.SYNOPSIS
    Checks whether Windows (Media Foundation) can play a video file, and why not.

.DESCRIPTION
    Opens the file with Media Foundation, the Windows component Duplicate File Finder's
    preview player uses, and tries to decode one picture and one sound frame. The
    report says which part works and which fails (e.g. a missing decoder), plus the
    Windows and browser-engine versions. It is saved next to this script as
    video-test.txt and copied to the clipboard.

    Run it on the PC where the preview fails:
        powershell -ExecutionPolicy Bypass -File .\Test-VideoPlayback.ps1 "D:\path\to\video.mp4"

    It only reads the file; nothing is changed.
#>
param([Parameter(Mandatory = $true)][string]$File)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MfProbe
{
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType
    {
        // IMFAttributes, in order; only GetGUID (8th) and SetGUID (22nd) are used
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble();
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob();
        void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble();
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(int stream, int index, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
        [PreserveSig] int ReadSample(int stream, int control, out int actualStream, out int flags, out long timestamp, out IntPtr sample);
    }

    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

    static Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static Guid SubType = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
    static Guid Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
    // NV12: what video decoders output (they can't produce RGB without a converter)
    static Guid Rgb32 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
    static Guid Pcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
    const int FirstVideo = unchecked((int)0xFFFFFFFC), FirstAudio = unchecked((int)0xFFFFFFFD);

    public static string Describe(int hr)
    {
        switch ((uint)hr)
        {
            case 0: return "OK";
            case 0xC00D5212: return "no decoder installed for this format (MF_E_TOPO_CODEC_NOT_FOUND)";
            case 0xC00D36B4: return "the decoder refused this format (MF_E_INVALIDMEDIATYPE)";
            case 0xC00D36B3: return "the file has no such stream (MF_E_INVALIDSTREAMNUMBER)";
            case 0xC00D36C4: return "the file type is not supported (MF_E_UNSUPPORTED_BYTESTREAM_TYPE)";
            case 0x80070002: return "file not found";
            case 0x80070005: return "access denied";
            default: return "error 0x" + hr.ToString("X8");
        }
    }

    // The codec as text: four-character code for most formats, or a known name
    static string Codec(Guid g)
    {
        string text = g.ToString();
        if (text.EndsWith("-0000-0010-8000-00aa00389b71"))
        {
            uint code = BitConverter.ToUInt32(g.ToByteArray(), 0);
            if (code == 0x1610) return "AAC";
            if (code == 0x1600) return "AAC (ADTS)";
            if (code == 0x55) return "MP3";
            if (code > 0xFFFF)
            {
                var b = BitConverter.GetBytes(code);
                return System.Text.Encoding.ASCII.GetString(b);
            }
            return "audio format 0x" + code.ToString("X4");
        }
        return text;
    }

    public static string Test(string file)
    {
        var report = new System.Text.StringBuilder();
        int hr = MFStartup(0x20070, 0);
        report.AppendLine("Media Foundation startup: " + Describe(hr));
        if (hr != 0) return report.ToString();
        IMFSourceReader reader;
        hr = MFCreateSourceReaderFromURL(file, IntPtr.Zero, out reader);
        report.AppendLine("Open the file: " + Describe(hr));
        if (hr != 0) return report.ToString();
        foreach (var kind in new[] { "Picture", "Sound" })
        {
            int stream = kind == "Picture" ? FirstVideo : FirstAudio;
            IMFMediaType native;
            hr = reader.GetNativeMediaType(stream, 0, out native);
            if (hr != 0) { report.AppendLine(kind + ": " + Describe(hr)); continue; }
            Guid sub;
            native.GetGUID(ref SubType, out sub);
            report.AppendLine(kind + " format: " + Codec(sub));
            // Asking for decoded output makes Media Foundation load the decoder
            IMFMediaType wanted;
            MFCreateMediaType(out wanted);
            Guid major = kind == "Picture" ? Video : Audio, output = kind == "Picture" ? Rgb32 : Pcm;
            wanted.SetGUID(ref MajorType, ref major);
            wanted.SetGUID(ref SubType, ref output);
            hr = reader.SetCurrentMediaType(stream, IntPtr.Zero, wanted);
            report.AppendLine("  " + kind + " decoder: " + Describe(hr));
            if (hr != 0) continue;
            int actual, flags; long time; IntPtr sample;
            hr = reader.ReadSample(stream, 0, out actual, out flags, out time, out sample);
            report.AppendLine("  Decoding the first " + (kind == "Picture" ? "frame" : "sound") + ": " + Describe(hr) +
                              (hr == 0 && sample == IntPtr.Zero ? " (no data returned, flags 0x" + flags.ToString("X") + ")" : ""));
            if (sample != IntPtr.Zero) Marshal.Release(sample);
        }
        return report.ToString();
    }
}
'@

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Video playback test - $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$lines.Add("Windows: $($os.Caption) (build $($os.BuildNumber))")
$mshtml = Get-Item "$env:SystemRoot\System32\mshtml.dll" -ErrorAction SilentlyContinue
$lines.Add("Browser engine (mshtml.dll): $(if ($mshtml) { $mshtml.VersionInfo.ProductVersion } else { 'missing' })")
foreach ($dll in 'mfplat.dll', 'mfreadwrite.dll', 'msmpeg2vdec.dll', 'msauddecmft.dll', 'MSAMRNBDecoder.dll') {
    $d = Get-Item "$env:SystemRoot\System32\$dll" -ErrorAction SilentlyContinue
    $what = @{ 'mfplat.dll' = 'Media Foundation'; 'mfreadwrite.dll' = 'Media Foundation reader'; 'msmpeg2vdec.dll' = 'H.264 video decoder';
               'msauddecmft.dll' = 'AAC/MP3 audio decoder'; 'MSAMRNBDecoder.dll' = 'AMR audio decoder' }[$dll]
    $lines.Add(("{0,-26} {1}" -f "$what :", $(if ($d) { "present ($($d.VersionInfo.ProductVersion))" } else { 'MISSING' })))
}
try {
    $feature = Get-WindowsFeature Server-Media-Foundation -ErrorAction Stop
    $lines.Add("Windows feature Server-Media-Foundation: $($feature.InstallState)")
} catch { }
$lines.Add("")
$lines.Add("File: $File")
if (-not (Test-Path -LiteralPath $File)) { $lines.Add("  file not found") }
else {
    $lines.Add("  size: $((Get-Item -LiteralPath $File).Length) bytes")
    try { foreach ($l in ([MfProbe]::Test((Resolve-Path -LiteralPath $File).ProviderPath) -split "`r?`n")) { if ($l) { $lines.Add("  " + $l) } } }
    catch { $lines.Add("  test failed: $($_.Exception.Message)") }
}
$out = Join-Path $PSScriptRoot 'video-test.txt'
$lines | Set-Content -LiteralPath $out -Encoding UTF8
try { $lines -join "`r`n" | Set-Clipboard } catch { }
$lines
"`nSaved to $out (and copied to the clipboard)."
