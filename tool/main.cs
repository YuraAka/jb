// dotnet create console -n <name>
// dotnet build --output ~/.jb/build /p:BaseIntermediateOutputPath=../../.jb/build/

using System;
using System.CommandLine;
using System.IO;
using YamlDotNet.Serialization;
using System.Diagnostics;

class Makefile
{
    public string type { get; set; }
    public string name { get; set; }
}

class Program
{
    const string JbRoot = "/Users/yuraaka/.jb";
    const string JbBuildRoot = $"{JbRoot}/build";
    const string RepoRoot = "/Users/yuraaka/dev";

    static int Main(string[] args)
    {
        var build = new Command("build");
        build.AddAlias("b");
        build.SetHandler((_) =>
        {
            var data = File.ReadAllText("jb.yaml");
            var deserializer = new DeserializerBuilder().Build();
            var makefile = deserializer.Deserialize<Makefile>(data);
            Console.WriteLine($"Type: {makefile.type}");

            // todo create mirror hierarchy
            Directory.CreateDirectory($"{JbBuildRoot}/jb/sample");
            if (!Directory.Exists($"{JbBuildRoot}/jb/sample/_")) {
                File.CreateSymbolicLink($"{JbBuildRoot}/jb/sample/_", $"{RepoRoot}/jb/sample");
            }

            {
                using var writer = File.CreateText($"{JbBuildRoot}/jb/sample/CMakeLists.txt");
                writer.WriteLine($@"
                    cmake_minimum_required(VERSION 3.12)
                    project({makefile.name}-prj)
                    file(GLOB SOURCES _/*.cpp _/*.c)
                    add_executable({makefile.name} ${{SOURCES}})
                ");
            }

            var ec = RunExternal("cmake", ".", $"{JbBuildRoot}/jb/sample");
            if (ec != 0) {
                Console.WriteLine($"CMake failed with {ec}");
                return;
            }

            ec = RunExternal("make", "", $"{JbBuildRoot}/jb/sample");
            if (ec != 0) {
                Console.WriteLine($"Make failed with {ec}");
                return;
            }

            if (!File.Exists($"{RepoRoot}/jb/sample/{makefile.name}")) {
                File.CreateSymbolicLink($"{RepoRoot}/jb/sample/{makefile.name}.symlink", $"{JbBuildRoot}/jb/sample/{makefile.name}");
            }
        });

        var root = new RootCommand();
        root.AddCommand(build);
        return root.Invoke(args);
    }

    static int RunExternal(string cmd, string args, string wd) {
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
            if (process == null) {
                throw new Exception("bad cmd");
            }
            // Read the standard output if needed
            string output = process.StandardOutput.ReadToEnd();
            Console.WriteLine("Output:");
            Console.WriteLine(output);

            // Wait for the process to exit
            process.WaitForExit();
            return process.ExitCode;
        }
    }
}

