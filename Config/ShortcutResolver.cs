using System.Runtime.InteropServices;

namespace GameStream.Server.Config;

/// <summary>Lê o destino de um atalho (.lnk) usando o WScript.Shell do Windows.</summary>
public static class ShortcutResolver
{
    public readonly record struct Info(string Target, string Args, string WorkingDir);

    public static Info? Resolve(string lnkPath)
    {
        object? shell = null;
        object? link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;

            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            link = sh.CreateShortcut(lnkPath);
            dynamic l = link!;

            string target = (string)l.TargetPath ?? "";
            string args = (string)l.Arguments ?? "";
            string workDir = (string)l.WorkingDirectory ?? "";
            return new Info(target, args, workDir);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
