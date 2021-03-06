﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitArchive
{
    class Program : ConsoleAppBase
    {
        static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    var location = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    var dir = Path.GetDirectoryName(location);
                    var fileName = Path.GetFileNameWithoutExtension(location);
                    config.AddJsonFile(Path.Combine(dir, $"{fileName}.config.json"), true);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.Configure<GitArchiveOptions>(hostContext.Configuration.GetSection(GitArchiveOptions.SectionName));
                })
                .RunConsoleAppFrameworkAsync(args);
        }
    }

    public class Json : ConsoleAppBase
    {
        public const string SettingsFile = @"git_archive.json";

        public static (ArchiveSettings, bool) GetSettings()
        {
            var fileName = SettingsFile;
            var onGitFolder = false;
            if (!File.Exists(fileName))
            {
                fileName = Path.Combine(".git", fileName);
                onGitFolder = true;
            }
            var json = File.ReadAllText(fileName);
            var settings = System.Text.Json.JsonSerializer.Deserialize<ArchiveSettings>(json);
            return (settings, onGitFolder);
        }

        public static void SaveSettings(ArchiveSettings settings, bool saveToGitFolder = false)
        {
            var fileName = saveToGitFolder ? Path.Combine(".git", SettingsFile) : SettingsFile;
            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(fileName, json);
        }

        public void Create(
            [Option(0, "archive folder")] string archiveFolder,
            [Option("b")] string[] branches = default,
            [Option("g")] bool saveToGitFolder = false)
        {
            var settings = new ArchiveSettings
            {
                ArchiveFolder = archiveFolder,
                Branches = (branches ?? new[] { "master" }).ToHashSet()
            };
            SaveSettings(settings, saveToGitFolder);
        }

        public void Archive([Option(0, "set archive folder")] string archiveFolder)
        {
            var (settings, onGitFolder) = GetSettings();
            settings.ArchiveFolder = archiveFolder;
            SaveSettings(settings, onGitFolder);
        }

        public void Add([Option(0, "branch")] string branch)
        {
            var (settings, onGitFolder) = GetSettings();
            settings.Branches.Add(branch);
            SaveSettings(settings, onGitFolder);
        }
    }

    public class Archive : ConsoleAppBase
    {
        private const string ModifyCacheFileName = "git_archive_cache.txt";
        private GitArchiveOptions _options;
        private Dictionary<string, DateTime> _modifyCache = null;

        public Archive(IOptions<GitArchiveOptions> options)
        {
            _options = options.Value;
        }

        public void Walk([Option("s")] bool silent = false)
        {
            var folders = _options.Folders;
            if (folders == null)
            {
                Context.Logger.LogWarning("folders not defined.");
                return;
            }
            var errors = folders.SelectMany(folder => Directory.GetFiles(folder, Json.SettingsFile, SearchOption.AllDirectories))
                .SelectMany(file =>
                {
                    var dir = Path.GetDirectoryName(file);
                    Directory.SetCurrentDirectory(dir);
                    return Execute_();
                })
                .ToList();

            if (!silent && errors.Count > 0)
            {
                var dir = Path.GetDirectoryName(_options.NotifyFile);
                var fileName = Path.GetFileNameWithoutExtension(_options.NotifyFile);
                var ext = Path.GetExtension(_options.NotifyFile);
                var outfile = Path.Combine(dir, $"{fileName}-{DateTime.Now:yyyyMMddHHmmss}{ext}");
                File.WriteAllLines(outfile, errors);
            }
        }

        public void Execute() => Execute_();

        private IEnumerable<string> Execute_()
        {
            var dir = Directory.GetCurrentDirectory();
            if (Path.GetFileName(dir) == ".git")
            {
                dir = Path.GetDirectoryName(dir);
                Directory.SetCurrentDirectory(dir);
            }
            var repo = Path.GetFileName(dir);
            var (settings, _) = Json.GetSettings();
            var results = new List<string>();
            var hasError = false;

            results.Add(dir);
            Context.Logger.LogInformation("Check {dir}", dir);

            _modifyCache = _modifyCache ?? GetModifyCache();

            var (_, status) = GitStatus();
            if (!status.Contains("working tree clean"))
            {
                Context.Logger.LogInformation("Working tree is not clean.");

                if (_modifyCache.TryGetValue(dir, out DateTime last))
                {
                    var log = GitLogOne("HEAD");
                    if (last < log.Date)
                    {
                        last = log.Date;
                        _modifyCache[dir] = last;
                        SaveModifyCache(_modifyCache);
                    }

                    Context.Logger.LogInformation("latest checking date {date}.", last);

                    if (DateTime.Now - last > TimeSpan.FromDays(30))
                    {
                        Context.Logger.LogError("Over 30 days.");

                        results.Add("    そろそろコミットしませんか。");
                        hasError = true;
                    }
                }
                else
                {
                    _modifyCache.Add(dir, DateTime.Now);
                    SaveModifyCache(_modifyCache);

                    Context.Logger.LogInformation("first check");
                }
            }
            else
            {
                Context.Logger.LogInformation("Working tree is clean.");

                if (_modifyCache.ContainsKey(dir))
                {
                    _modifyCache.Remove(dir);
                    SaveModifyCache(_modifyCache);
                }
            }

            var remotes = GitCommand("remote").stdout;
            foreach (var remote in remotes)
            {
                Context.Logger.LogInformation("Check remote {remote}", remote);

                var (exitCode, lines, errors) = GitCommand($"remote show {remote}");
                if (exitCode == 0)
                {
                    Context.Logger.LogInformation("{lines}", string.Join("\n", lines));

                    var unmatches = lines
                        .SkipWhile(line => !line.Contains("Local refs configured for 'git push':"))
                        .Skip(1)
                        .Where(line => !line.EndsWith("(up to date)"))
                        .ToList();
                    if (unmatches.Count > 0)
                    {
                        results.Add($"    push or pill {remote}");
                        foreach (var branch in unmatches)
                        {
                            results.Add($"    {branch}");
                        }
                        hasError = true;
                    }
                }
                else
                {
                    Context.Logger.LogError("{errors}", string.Join("\n", errors));

                    results.Add($"    remote show {remote} fail.");
                    foreach (var line in errors)
                    {
                        results.Add($"    {line}");
                    }
                    hasError = true;
                }
            }

            if (settings.ArchiveFolder != "-")
            {
                if (!Directory.Exists(settings.ArchiveFolder))
                {
                    var message = $"archive folder not exists. [{settings.ArchiveFolder}]";
                    Context.Logger.LogError("[{dir}] {message}", dir, message);
                    results.Add("    ソースファイル保存先 未設定");
                    return results;
                }
                var tags = GitCommand("tag").stdout.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
                foreach (var tag in tags)
                {
                    var outfile = Path.Combine(settings.ArchiveFolder, $"{repo}-{tag}.zip");
                    if (!File.Exists(outfile))
                    {
                        GitCommand($"archive -o \"{outfile}\" {tag}");
                        Context.Logger.LogInformation("archive tag {tag}", tag);
                    }
                }
                foreach (var branch in settings.Branches)
                {
                    var basefile = Path.Combine(settings.ArchiveFolder, $"{repo}-{branch}-latest");
                    var (isUpdate, commit) = IsUpdateBranch(basefile, branch);
                    if (isUpdate)
                    {
                        GitCommand($"archive -o \"{basefile}.zip\" {branch}");
                        File.WriteAllText($"{basefile}.ref", commit);
                        Context.Logger.LogInformation("archive branch {branch}", branch);
                    }
                }
            }
            return hasError ? results : Enumerable.Empty<string>();
        }

        private Dictionary<string, DateTime> GetModifyCache()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cacheFile = Path.Combine(home, ModifyCacheFileName);
            if (File.Exists(cacheFile))
            {
                return File.ReadAllLines(cacheFile)
                .Select(line => line.Split('\t'))
                .ToDictionary(a => a[0], a => DateTime.Parse(a[1]));
            }
            else
            {
                return new Dictionary<string, DateTime>();
            }
        }

        private void SaveModifyCache(Dictionary<string, DateTime> cache)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cacheFile = Path.Combine(home, ModifyCacheFileName);
            File.WriteAllLines(cacheFile, cache.Select(a => $"{a.Key}\t{a.Value:o}"));
        }

        private (bool isUpdate, string commit) IsUpdateBranch(string basefile, string branch)
        {
            var reffile = basefile + ".ref";
            var oldCommit = string.Empty;
            if (File.Exists(reffile))
            {
                oldCommit = File.ReadLines(reffile).First();
            }
            var commit = GitCommand($"log -1 {branch}").stdout.First().Split(" ")[1];
            return (oldCommit != commit, commit);
        }

        private (string branch, string status) GitStatus()
        {
            var (_, lines, _) = GitCommand("status");
            var branch = lines[0].Split(" ").Last();
            var status = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Last();
            return (branch, status);
        }

        private GitLogItem GitLogOne(string branch)
        {
            var (_, lines, _) = GitCommand($"log -1 {branch}");
            var log = new GitLogItem();
            List<string> comment = null;
            foreach (var line in lines)
            {
                if (comment == null)
                {
                    if (line.StartsWith("commit"))
                    {
                        log.Commit = line.Split(" ")[1];
                    }
                    else if (string.IsNullOrWhiteSpace(line))
                    {
                        comment = new List<string>();
                    }
                    else
                    {
                        var index = line.IndexOf(":");
                        if (index > 0)
                        {
                            var key = line.Substring(0, index);
                            var value = line.Substring(index + 1).Trim();
                            if (key == "Author")
                            {
                                log.Author = value;
                            }
                            else if (key == "Date")
                            {
                                var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                                log.Date = DateTime.ParseExact(value, "ddd MMM d HH:mm:ss yyyy zzz", culture);
                            }
                        }
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        comment.Add(line.Trim());
                    }
                }
            }
            if (comment != null)
            {
                log.Comment = string.Join("\n", comment);
            }
            return log;
        }

        private (int exitCode, string[] stdout, string[] stderr) GitCommand(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit();
                var stdout = p.StandardOutput.ReadToEnd().Split('\n');
                var stderr = p.StandardError.ReadToEnd().Split('\n');
                return (p.ExitCode, stdout, stderr);
            }
        }

        private class GitLogItem
        {
            public string Commit { get; set; }
            public string Author { get; set; }
            public DateTime Date { get; set; }
            public string Comment { get; set; }
        }

    }

    public class GitArchiveOptions
    {
        public const string SectionName = "Archive";

        public string[] Folders { get; set; }
        public string NotifyFile { get; set; }
    }
    
    public class ArchiveSettings
    {
        public string ArchiveFolder { get; set; }
        public HashSet<string> Branches { get; set; }
    }
}
