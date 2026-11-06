namespace MCLneo.Core.Logs;

public sealed record CrashFinding(string Title, string Detail, string Severity);

public static class CrashAnalyzer
{
    public static IReadOnlyList<CrashFinding> Analyze(string text)
    {
        var findings = new List<CrashFinding>();
        void Add(string title, string detail, string severity = "warning")
        {
            if (findings.All(f => f.Title != title))
            {
                findings.Add(new CrashFinding(title, detail, severity));
            }
        }

        if (text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Java heap space", StringComparison.OrdinalIgnoreCase))
        {
            Add("内存不足", "在设置中提高内存分配，或减少同时加载的模组/光影。", "error");
        }

        if (text.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("class file version", StringComparison.OrdinalIgnoreCase))
        {
            Add("Java 版本不匹配", "当前 Java 版本低于游戏或模组要求。安装对应版本的 Java，或在设置中切换运行时。", "error");
        }

        if (text.Contains("Duplicate mods", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("duplicate mod", StringComparison.OrdinalIgnoreCase))
        {
            Add("模组重复", "同一个模组存在多个版本。删除 mods 目录中重复的 jar 后重试。", "error");
        }

        if (text.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("requires", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("which is missing", StringComparison.OrdinalIgnoreCase))
        {
            Add("缺少前置模组", "安装日志中列出的必选依赖，或使用启动器的依赖安装功能。", "error");
        }

        if (text.Contains("Mixin apply failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MixinTransformerError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("mixin.injection.throwables", StringComparison.OrdinalIgnoreCase))
        {
            Add("Mixin 冲突", "常见于模组与游戏版本不匹配。确认所有模组支持当前 Minecraft/加载器版本。", "error");
        }

        if (text.Contains("NoSuchMethodError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NoClassDefFoundError", StringComparison.OrdinalIgnoreCase))
        {
            Add("模组或运行库不兼容", "某个模组调用了不存在的类/方法，通常需要更新模组、加载器或游戏版本。", "warning");
        }

        if (text.Contains("GLFW error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to create display", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Pixel format", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("OpenGL", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            Add("显卡/OpenGL 初始化失败", "更新显卡驱动；远程桌面或虚拟机可能不支持 OpenGL，请在本机桌面运行。", "warning");
        }

        if (text.Contains("AccessDeniedException", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            Add("文件权限/占用", "关闭占用文件的程序（游戏、杀毒软件），并确认游戏目录可写。", "warning");
        }

        if (text.Contains("The game crashed whilst", StringComparison.OrdinalIgnoreCase))
        {
            var line = text.Split('\n').FirstOrDefault(l =>
                l.Contains("The game crashed whilst", StringComparison.OrdinalIgnoreCase));
            Add("游戏崩溃报告", line?.Trim() ?? "查看完整崩溃报告定位原因。", "info");
        }

        return findings;
    }
}
