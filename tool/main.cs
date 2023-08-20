// dotnet create console -n <name>
// dotnet build --output ~/.jb/build /p:BaseIntermediateOutputPath=../../.jb/build/

/*
plan:
    - deps for libs recursively go to exe
    - auto name exe/lib (lib with prefix)
    - do not process already built items
    - cross-platform home dir obtain
    - restrict commiting symlinks
*/

using System.CommandLine;
using YamlDotNet.Serialization;
using System.Diagnostics;
using SmartFormat;

class ProjectSpec
{
    public enum Type
    {
        lib,    ///< most common case, type may be omitted
        exe
    }

    public Type type { get; set; } = Type.lib;
    public string? name { get; set; }
    public string[] deps {get; set; } = new string[0];
}

class CMakeTemplate
{
    public class Context
    {
        public string? ProjectName { get; set; }
        public string? BuildRoot { get; set; }
        public string? SrcRoot { get; set; }

        public string? SrcPaths { get; set; }

        public string? DepLibNames { get; set; }
        public string? DepLibPaths { get; set; }
    }

    static string ExeTemplate = @"
cmake_minimum_required(VERSION 3.12)
project({ProjectName})
file(GLOB SOURCES {SrcPaths})

add_executable({ProjectName} $\{SOURCES\})
find_library(LIB_DEPS
    NAMES {DepLibNames}
    PATHS {DepLibPaths}
)

target_link_libraries(exe2 PRIVATE $\{LIB_DEPS\})
target_include_directories({ProjectName} PUBLIC
    {SrcRoot}
    $\{CMAKE_CURRENT_SOURCE_DIR\}
)
";

    static string LibTemplate = @"
cmake_minimum_required(VERSION 3.12)
project({ProjectName})
file(GLOB SOURCES _/*.cpp _/*.c)

add_library({ProjectName} STATIC $\{SOURCES\})
target_include_directories({ProjectName} PUBLIC {SrcRoot} $\{CMAKE_CURRENT_SOURCE_DIR\})
";

    static public string ComposeExe(Context context)
    {
        return Smart.Format(ExeTemplate, context);
    }

    static public string ComposeLib(Context context)
    {
        return Smart.Format(LibTemplate, context);
    }
}

class Program
{
    const string JbRoot = "/Users/yuraaka/.jb";
    const string BuildRoot = $"{JbRoot}/build";
    const string ProjectConfigFileName = "jb.yaml";

    static int Main(string[] args)
    {
        var build = new Command("build");
        build.AddAlias("b");
        build.SetHandler((_) => {
            try {
                Build();
            } catch (Exception err) {
                PrintFail(err.Message);
            }
        });

        var clean = new Command("clean");
        clean.SetHandler((_) => { Clean();});

        var root = new RootCommand();
        root.AddCommand(build);
        root.AddCommand(clean);

        return root.Invoke(args);
    }

    static ProjectSpec ReadProject(string path)
    {
        var data = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder().Build();
        var target = deserializer.Deserialize<ProjectSpec>(data);
        if (target.name == null) {
            target.name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)));
        }

        return target;
    }

    static string GetProjectDirectory(string dir, string rootDir)
    {
        string cfgPath = Path.Combine(dir, ProjectConfigFileName);
        if (Path.Exists(cfgPath))
        {
            return dir;
        }

        if (dir == rootDir)
        {
            throw new Exception($"Cannot locate {ProjectConfigFileName} in parent hierarchy");
        }

        return GetProjectDirectory(Directory.GetParent(dir)!.FullName, rootDir);
    }

    static void Build()
    {
        var srcRoot = GetRepositoryRoot();
        var srcDir = GetProjectDirectory(Directory.GetCurrentDirectory(), srcRoot);
        var project = ReadProject(Path.Combine(srcDir, ProjectConfigFileName));
        BuildProject(project, srcRoot, srcDir);
        PrintOk();
    }

    static void PrintOk()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Ok");
        Console.ResetColor();
    }

    static void PrintFail(string message)
    {
        Console.WriteLine(message);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Failed");
        Console.ResetColor();
    }

    static void Clean()
    {
        if (Directory.Exists($"{BuildRoot}"))
        {
            Directory.Delete($"{BuildRoot}", true);
        }
    }

    static string MirrorHierarchy(string srcRoot, string fromDir, string toRoot)
    {
        var relPath = Path.GetRelativePath(srcRoot, fromDir);
        var targetPath = Path.Combine(toRoot, relPath);
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        return targetPath;
    }

    static int BuildProject(ProjectSpec project, string srcRoot, string srcDir, bool makeLink = true)
    {
        var buildDir = MirrorHierarchy(srcRoot, srcDir, BuildRoot);
        if (!Directory.Exists($"{buildDir}/_"))
        {
            File.CreateSymbolicLink($"{buildDir}/_", $"{srcDir}");
        }

        GenerateCMakeLists(project, buildDir, srcRoot, srcDir);
        RunExternal("cmake", ".", buildDir);
        RunExternal("cmake", "--build .", buildDir);
        if (makeLink && project.type == ProjectSpec.Type.exe) {
            // todo problems on Windows? project.name.exe
            var binSymlink = Path.Combine(srcDir, $"{project.name}");
            if (File.Exists(binSymlink))
            {
                File.Delete(binSymlink);
            }

            File.CreateSymbolicLink(binSymlink, Path.Combine(buildDir, $"{project.name}"));
        }

        return 0;
    }

    static bool IsSymbolicLink(string directoryPath)
    {
        FileAttributes attributes = File.GetAttributes(directoryPath);
        return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    static List<string> CollectProjectSubdirs(string srcDir, bool top)
    {
        var result = new List<string>(){srcDir};
        foreach (string subDir in Directory.GetDirectories(srcDir))
        {
            var path = Path.Combine(srcDir, subDir);
            if (IsSymbolicLink(path)) {
                // does not follow symlinks
                continue;
            }

            if (top || !File.Exists(Path.Combine(path, ProjectConfigFileName)))
            {
                result.AddRange(CollectProjectSubdirs(path, false));
            }
        }

        return result;
    }
    static void GenerateCMakeLists(ProjectSpec project, string buildDir, string srcRoot, string srcDir)
    {
        using var writer = File.CreateText($"{buildDir}/CMakeLists.txt");
        string[] srcExts = {"*.cpp", "*.c"};
        var srcPaths = CollectProjectSubdirs(srcDir, true);
        List<string> srcPathExts = new List<string>();
        foreach (var srcPath in srcPaths)
        {
            var relSrcPath = Path.GetRelativePath(srcDir, srcPath);
            foreach (var srcExt in srcExts)
            {
                srcPathExts.Add(Path.Combine(relSrcPath, srcExt));
            }
        }

        var srcSep = "\n    _/";
        var context = new CMakeTemplate.Context
        {
            SrcRoot = srcRoot,
            BuildRoot = BuildRoot,
            SrcPaths = srcSep + string.Join(srcSep, srcPathExts) + "\n",
            ProjectName = project.name
        };

        /// todo: expand to dll
        if (project.deps.Length > 0 && project.type == ProjectSpec.Type.exe)
        {
            var names = new List<string>();
            var paths = new List<string>();
            foreach (var dep in project.deps)
            {
                var depSrcDir = Path.Combine(srcRoot, dep);
                var depTarget = ReadProject(Path.Combine(depSrcDir, ProjectConfigFileName));
                names.Add(depTarget.name!);
                paths.Add(Path.Combine(BuildRoot, dep));
                BuildProject(depTarget, srcRoot, depSrcDir);
            }

            context.DepLibNames = string.Join(" ", names);
            context.DepLibPaths = string.Join(" ", paths);
        }

        switch (project.type)
        {
            case ProjectSpec.Type.exe:
                writer.WriteLine(CMakeTemplate.ComposeExe(context));
                break;
            case ProjectSpec.Type.lib:
                writer.WriteLine(CMakeTemplate.ComposeLib(context));
                break;
            default:
                throw new ArgumentException($"Unknown type: {project.type}");
        }
    }

    static void RunExternal(string cmd, string args, string wd)
    {
        ProcessStartInfo external = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(external))
        {
            if (process == null)
            {
                throw new Exception("bad cmd");
            }
            // Read the standard output if needed
            //string output = process.StandardOutput.ReadToEnd();
            //Console.WriteLine("Output:");
            //Console.WriteLine(output);

            // Wait for the process to exit
            process.WaitForExit();
            if (process.ExitCode != 0) {
                var error = process.StandardError.ReadToEnd();
                Console.WriteLine(error);
                throw new Exception($"Failed to run {cmd} {args} from dir {wd}");
            }
        }
    }

    static string GetRepositoryRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(currentDir, ".jb.root")))
        {
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir == null)
            {
                throw new Exception("Outside of repository");
            }

            currentDir = parentDir.FullName;
        }

        return currentDir;
    }
}

