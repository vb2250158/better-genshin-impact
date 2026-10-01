using System;
using System.IO;
using System.Text;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Model;
using Newtonsoft.Json;

// No App, host, VM, game/capture lookup, input, protocol or network startup.
if (args.Length != 4 || args[0] != "--name" || args[2] != "--output") return 2;
var name = args[1];
var path = args[3];
if (string.IsNullOrWhiteSpace(name) || !Path.IsPathFullyQualified(path)) return 2;
try
{
    var config = new OneDragonFlowConfig { Name = name };
    foreach (var task in OneDragonTaskDefaults.Create())
    {
        config.TaskDefinitions.Add(task.Id, task.Name);
        config.TaskEnabledList.Add(task.Id, task.IsEnabled);
        config.TaskOrder.Add(task.Id);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    // CreateNew forbids overwriting an existing official/user configuration.
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(file, new UTF8Encoding(false));
    writer.Write(JsonConvert.SerializeObject(config, Formatting.Indented));
    return 0;
}
catch { return 1; }
