﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Construction;
using static ModernRonin.ProjectRenamer.Executor;
using static ModernRonin.ProjectRenamer.Runtime;

namespace ModernRonin.ProjectRenamer
{
    public class Application
    {
        readonly Configuration _configuration;
        readonly Encoding _projectFileEncoding = Encoding.UTF8;
        readonly string _solutionPath;

        public Application(Configuration configuration, string solutionPath)
        {
            _configuration = configuration;
            _solutionPath = solutionPath;
        }

        void EnsureGitIsClean()
        {
            run("update-index -q --refresh");
            run("diff-index --quiet --cached HEAD --");
            run("diff-files --quiet");
            run("ls-files --exclude-standard --others");

            void run(string arguments) =>
                Tool("git", arguments,
                    () => Error("git does not seem to be clean, check git status"));
        }

        public void Run()
        {
            EnsureGitIsClean();

            var (wasFound, oldProjectPath, solutionFolderPath) = findProject();
            if (!wasFound) Error($"{_configuration.OldProjectName} cannot be found in the solution");

            var oldDir = Path.GetDirectoryName(oldProjectPath);
            var newDir = Path.Combine(Path.GetDirectoryName(oldDir), _configuration.NewProjectName);
            var newProjectPath =
                Path.Combine(newDir, $"{_configuration.NewProjectName}{Constants.ProjectFileExtension}");

            removeFromSolution();
            gitMove();
            replaceReferences();
            addToSolution();
            updatePaket();
            stageAllChanges();

            if (DoesUserAgree("Finished - do you want to run a dotnet build to see whether all went well?"))
            {
                Tool("dotnet", "build", () =>
                {
                    if (DoesUserAgree(
                        "dotnet build returned an error or warning - do you want to rollback all changes?"))
                    {
                        RollbackGit();
                        Abort();
                    }
                });
            }

            if (DoesUserAgree("Do you want me to create a commit for you?"))
            {
                var arguments =
                    $"commit -m \"Renamed {_configuration.OldProjectName} to {_configuration.NewProjectName}\"";
                Tool("git", arguments,
                    () => { Console.Error.WriteLine($"'git {arguments}' failed"); });
            }

            void stageAllChanges() => Git("add .");

            void updatePaket()
            {
                if (Directory.Exists(".paket") &&
                    DoesUserAgree("This solution uses paket - do you want to run paket install?"))
                    runDotNet("paket install");
            }

            void replaceReferences()
            {
                var projectFiles = Directory
                    .EnumerateFiles(".", $"*{Constants.ProjectFileExtension}", SearchOption.AllDirectories)
                    .ToList();
                var (oldReference, newReference) =
                    (searchPattern(_configuration.OldProjectName),
                        searchPattern(_configuration.NewProjectName));
                projectFiles.ForEach(replaceIn);

                void replaceIn(string projectFile)
                {
                    var contents = File.ReadAllText(projectFile, _projectFileEncoding);
                    contents = contents.Replace(oldReference, newReference);
                    File.WriteAllText(projectFile, contents, _projectFileEncoding);
                }

                string searchPattern(string name) =>
                    Path.Combine(name, name) + Constants.ProjectFileExtension;
            }

            void gitMove()
            {
                Git($"mv {oldDir} {newDir}");
                var oldPath = Path.Combine(newDir, Path.GetFileName(oldProjectPath));
                Git($"mv {oldPath} {newProjectPath}");
            }

            void addToSolution()
            {
                var solutionFolderArgument = string.IsNullOrWhiteSpace(solutionFolderPath)
                    ? string.Empty
                    : $"-s {solutionFolderPath}";
                runDotNet($"sln add {solutionFolderArgument} {newProjectPath}");
            }

            void removeFromSolution() => runDotNet($"sln remove {oldProjectPath}");

            void runDotNet(string arguments) => Tool("dotnet", arguments);

            (bool wasFound, string projectPath, string solutionFolder) findProject()
            {
                var solution = SolutionFile.Parse(_solutionPath);
                var project = solution.ProjectsInOrder.FirstOrDefault(p =>
                    p.ProjectName.EndsWith(_configuration.OldProjectName,
                        StringComparison.InvariantCultureIgnoreCase));
                return project switch
                {
                    null => (false, null, null),
                    _ when project.ParentProjectGuid == null => (true, project.AbsolutePath, null),
                    _ => (true, project.AbsolutePath,
                        path(solution.ProjectsByGuid[project.ParentProjectGuid]))
                };

                string path(ProjectInSolution p)
                {
                    if (p.ParentProjectGuid == null) return p.ProjectName;
                    var parent = solution.ProjectsByGuid[p.ParentProjectGuid];
                    var parentPath = path(parent);
                    return $"{parentPath}/{p.ProjectName}";
                }
            }
        }

        bool DoesUserAgree(string question)
        {
            Console.WriteLine($"{question} [Enter=Yes, any other key=No]");
            var key = Console.ReadKey();
            return key.Key == ConsoleKey.Enter;
        }
    }
}