using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PopTranslate.Launcher;

internal static class Program
{
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    private static int Main()
    {
        var rootDir = AppDomain.CurrentDomain.BaseDirectory;
        var runtimeDir = Path.Combine(rootDir, "runtime");
        var appExePath = Path.Combine(runtimeDir, "PopTranslate.App.exe");

        if (!File.Exists(appExePath))
        {
            MessageBoxW(
                IntPtr.Zero,
                "未找到核心运行组件：runtime\\PopTranslate.App.exe\n请确认解压完整的发布包目录结构。",
                "PopTranslate 启动失败",
                MbIconError);
            return 1;
        }

        try
        {
            var rawArgs = ExtractRawArguments(Environment.CommandLine);
            var startInfo = new ProcessStartInfo
            {
                FileName = appExePath,
                Arguments = rawArgs,
                WorkingDirectory = runtimeDir,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            return process is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(
                IntPtr.Zero,
                "启动 PopTranslate 失败：" + ex.Message,
                "PopTranslate 启动错误",
                MbIconError);
            return 1;
        }
    }

    private static string ExtractRawArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var trimmed = commandLine.TrimStart();
        var index = 0;

        if (trimmed.Length > 0 && trimmed[0] == '"')
        {
            index = 1;
            while (index < trimmed.Length && trimmed[index] != '"')
            {
                index++;
            }

            if (index < trimmed.Length)
            {
                index++;
            }
        }
        else
        {
            while (index < trimmed.Length && !char.IsWhiteSpace(trimmed[index]))
            {
                index++;
            }
        }

        return index < trimmed.Length ? trimmed.Substring(index).TrimStart() : string.Empty;
    }
}
