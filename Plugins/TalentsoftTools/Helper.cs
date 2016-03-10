﻿using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using GitCommands;
using GitUIPluginInterfaces;

namespace TalentsoftTools
{
    public class Helper
    {
        public static bool ExitVisualStudio(string solutionFileName)
        {
            solutionFileName = Path.GetFileNameWithoutExtension(solutionFileName);
            var process = Process.GetProcessesByName("devenv");
            foreach (Process p in process)
            {
                if (p.MainWindowTitle.Contains(solutionFileName))
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(); // possibly with a timeout
                    }
                    catch (Win32Exception winException)
                    {
                        return false;
                        // process was terminating or can't be terminated - deal with it
                    }
                    catch (InvalidOperationException invalidException)
                    {
                        return false;
                        // process has already exited - might be able to let this one go
                    }
                }
            }
            return true;
        }

        public static List<string> GetSolutionsFile(string directory, int currentDepth = 0)
        {
            var files = new List<string>();
            try
            {

                files.AddRange(Directory.GetFiles(directory, "*.sln").Select(Path.GetFileName));
                if (currentDepth < 3)
                {
                    foreach (string directoryItem in Directory.GetDirectories(directory))
                    {
                        files.AddRange(GetSolutionsFile(directoryItem, currentDepth + 1));
                    }
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show("Error when loading solution files. " + exception);
            }
            return files;
        }

        public static bool Build(string solutionFileFullPath, string pathToMsBuild)
        {
            //string validPathToMsBuild = GetMsBuildPath();
            if (string.IsNullOrEmpty(pathToMsBuild) || string.IsNullOrEmpty(solutionFileFullPath))
            {
                return false;
            }
            return RunCommandLine(new List<string> { string.Format("\"{0}\" /t:Build /p:BuildInParallel=true /p:Configuration=Debug /maxcpucount \"{1}\"", pathToMsBuild, solutionFileFullPath) });
        }

        public static bool RunCommandLine(List<string> commands)
        {
            string output = string.Empty;
            string error = string.Empty;

            ProcessStartInfo processStartInfo = new ProcessStartInfo("cmd.exe");
            processStartInfo.RedirectStandardOutput = true;
            processStartInfo.RedirectStandardError = true;
            processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            processStartInfo.RedirectStandardInput = true;
            processStartInfo.UseShellExecute = false;
            processStartInfo.CreateNoWindow = true;

            Process process = Process.Start(processStartInfo);

            using (StreamWriter sw = process.StandardInput)
            {
                if (sw.BaseStream.CanWrite)
                {
                    foreach (var command in commands)
                    {
                        sw.WriteLine(command);
                    }
                }
            }

            using (StreamReader streamReader = process.StandardOutput)
            {
                output = streamReader.ReadToEnd();
            }

            using (StreamReader streamReader = process.StandardError)
            {
                error = streamReader.ReadToEnd();
            }
            process.WaitForExit();
            if (!string.IsNullOrEmpty(error))
            {
                return false;
            }
            return true;
        }

        public static bool LaunchVisualStudio(string solutionFileFullPath)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo(solutionFileFullPath) { UseShellExecute = true };
            try
            {
                Process.Start(processStartInfo);
            }
            catch (Exception ex)
            {
                return false;
            }
            return true;
        }

        public static bool LaunchWebUri(string uri)
        {
            try
            {
                Process.Start(new UriBuilder(uri).Uri.AbsoluteUri);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #region Git Helpers

        public static string[] GetBranchInfo(GitUIBaseEventArgs gitUiCommands, string branchName)
        {
            CmdResult result = gitUiCommands.GitModule.RunGitCmdResult(string.Format("log -n 1 --pretty=format:\" % an;% cr\" {0}", branchName));
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOutput) && result.StdOutput.Contains(";"))
            {
                return result.StdOutput.Split(';');
            }
            return new string[0];
        }

        public static List<GitRef> GetBranches(GitUIBaseEventArgs gitUiCommands)
        {
            return GetTreeRefs(gitUiCommands, gitUiCommands.GitModule.RunGitCmd("show-ref --dereference")).ToList();
        }

        static List<GitRef> GetTreeRefs(GitUIBaseEventArgs gitUiCommands, string tree)
        {
            var defaultHeadPattern = new Regex("refs/remotes/[^/]+/HEAD", RegexOptions.Compiled);
            var itemsStrings = tree.Split('\n');

            var gitRefs = new List<GitRef>();
            var defaultHeads = new Dictionary<string, GitRef>(); // remote -> HEAD
            var remotes = gitUiCommands.GitModule.GetRemotes(false);

            foreach (var itemsString in itemsStrings)
            {
                if (itemsString == null || itemsString.Length <= 42 || itemsString.StartsWith("error: "))
                    continue;

                var completeName = itemsString.Substring(41).Trim();
                var guid = itemsString.Substring(0, 40);
                var remoteName = GitCommandHelpers.GetRemoteName(completeName, remotes);
                var head = new GitRef(null, guid, completeName, remoteName);
                if (defaultHeadPattern.IsMatch(completeName))
                {
                    defaultHeads[remoteName] = head;
                }
                else
                {
                    gitRefs.Add(head);
                }
            }

            // do not show default head if remote has a branch on the same commit
            GitRef defaultHead;
            foreach (var gitRef in gitRefs.Where(head => defaultHeads.TryGetValue(head.Remote, out defaultHead) && head.Guid == defaultHead.Guid))
            {
                defaultHeads.Remove(gitRef.Remote);
            }

            gitRefs.AddRange(defaultHeads.Values);

            return gitRefs;
        }

        public static List<GitRef> GetLocalsBranches(GitUIBaseEventArgs gitUiCommands)
        {
            gitUiCommands.GitModule.RunGitCmd("git fetch -p -n");
            return GetBranches(gitUiCommands).Where(h => !h.IsRemote && !h.IsTag && !h.IsOther && !h.IsBisect).ToList();
        }

        public static List<GitRef> GetRemotesBranches(GitUIBaseEventArgs gitUiCommands)
        {
            gitUiCommands.GitModule.RunGitCmdResult("git fetch --all");
            return GetBranches(gitUiCommands).Where(h => h.IsRemote && !h.IsTag).ToList();
        }

        public static string[] GetUnmergerBranches(GitUIBaseEventArgs gitUiCommands)
        {
            gitUiCommands.GitModule.RunGitCmd("git fetch -p -n");
            CmdResult gitResult = gitUiCommands.GitModule.RunGitCmdResult("branch --no-merged");
            if (gitResult.ExitCode == 0)
            {
                return gitResult.StdOutput.Replace(" ", string.Empty).SplitLines();
            }
            return new string[0];
        }

        public static CmdResult DeleteMergedLocalBranch(GitUIBaseEventArgs gitUiCommands, string branchToDelete)
        {
            return gitUiCommands.GitModule.RunGitCmdResult(string.Format("branch -d {0}", branchToDelete));
        }

        public static CmdResult DeleteUnmergedLocalBranch(GitUIBaseEventArgs gitUiCommands, string branchToDelete)
        {
            return gitUiCommands.GitModule.RunGitCmdResult(string.Format("branch -D {0}", branchToDelete));
        }

        public static string[] GetStashs(GitUIBaseEventArgs gitUiCommands)
        {
            CmdResult gitResult = gitUiCommands.GitModule.RunGitCmdResult("stash list");
            if (gitResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(gitResult.StdOutput))
            {
                return gitResult.StdOutput.SplitLines();
            }
            return new string[0];
        }

        public static string[] GetDiff(GitUIBaseEventArgs gitUiCommands)
        {
            CmdResult gitResult = gitUiCommands.GitModule.RunGitCmdResult("diff");
            if (gitResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(gitResult.StdOutput))
            {
                return gitResult.StdOutput.SplitLines();
            }
            return new string[0];
        }

        #endregion
    }
}
