// dotnet create console -n <name>
// dotnet build --output ~/.jb/build /p:BaseIntermediateOutputPath=../../.jb/build/

using System;
using System.CommandLine;
using System.IO;
using YamlDotNet.Serialization;
using System.Diagnostics;

class JbYaml
{
    public string type { get; set; }
    public string name { get; set; }
}

class Program
{
    const string JbRoot = "/Users/yuraaka/.jb";
    const string JbBuildRoot = $"{JbRoot}/build";

    static int Main(string[] args)
    {
        var build = new Command("build");
        build.AddAlias("b");
        build.SetHandler((_) =>
        {
            var data = File.ReadAllText("jb.yaml");
            var deserializer = new DeserializerBuilder().Build();
            var jbYaml = deserializer.Deserialize<JbYaml>(data);
            var repoRoot = FindRepositoryRoot();
            if (repoRoot == null)
            {
                Console.WriteLine("outside repository");
                return;
            }

            var sourceDir = Directory.GetCurrentDirectory();
            var buildDir = MirrorHierarchy(repoRoot, sourceDir, JbBuildRoot);
            if (!Directory.Exists($"{buildDir}/_"))
            {
                File.CreateSymbolicLink($"{buildDir}/_", $"{sourceDir}");
            }

            GenerateCMakeLists(jbYaml, buildDir);
            var error = RunExternal("cmake", ".", buildDir);
            if (error != 0)
            {
                Console.WriteLine($"CMake failed with {error}");
                return;
            }

            error = RunExternal("make", "", buildDir);
            if (error != 0)
            {
                Console.WriteLine($"Make failed with {error}");
                return;
            }

            var binSymlink = Path.Combine(sourceDir, $"{jbYaml.name}");
            if (File.Exists(binSymlink))
            {
                File.Delete(binSymlink);
            }

            File.CreateSymbolicLink(binSymlink, Path.Combine(buildDir, $"{jbYaml.name}"));
        });

        var clean = new Command("clean");
        clean.SetHandler((_) =>
        {
            if (Directory.Exists($"{JbBuildRoot}"))
            {
                Directory.Delete($"{JbBuildRoot}", true);
            }
        });

        var root = new RootCommand();
        root.AddCommand(build);
        root.AddCommand(clean);

        return root.Invoke(args);
    }

    static string MirrorHierarchy(string repoRoot, string fromDir, string toRoot)
    {
        var relPath = Path.GetRelativePath(repoRoot, fromDir);
        var targetPath = Path.Combine(toRoot, relPath);
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        return targetPath;
    }

    static void GenerateCMakeLists(JbYaml project, string buildDir)
    {
        using var writer = File.CreateText($"{buildDir}/CMakeLists.txt");
        writer.WriteLine($@"
            cmake_minimum_required(VERSION 3.12)
            project({project.name})
            file(GLOB SOURCES _/*.cpp _/*.c)
            add_executable({project.name} ${{SOURCES}})
        ");
    }

    static int RunExternal(string cmd, string args, string wd)
    {
        ProcessStartInfo external = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
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
            return process.ExitCode;
        }
    }

    static string? FindRepositoryRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(currentDir, ".jb.root")))
        {
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir == null)
            {
                return null;
            }

            currentDir = parentDir.FullName;
        }

        return currentDir;
    }
}

