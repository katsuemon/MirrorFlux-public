using System.IO;
using System.IO.Compression;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class PaneNavigationRegression
{
    public static async Task Run()
    {
        void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
        var keys=new KeyBindingService();
        Check(keys.TryResolve(Key.Left,ModifierKeys.None,out var left)&&left.Command==AppCommand.ParentLeft,"Default left key");
        Check(keys.TryResolve(Key.Right,ModifierKeys.None,out var right)&&right.Command==AppCommand.ParentRight,"Default right key");
        var combinedLeft = left; var combinedRight = right;
        Check(left.FollowingCommands?.Single().Command == AppCommand.ActivateLeftPane && right.FollowingCommands?.Single().Command == AppCommand.ActivateRightPane, "Default sequences");
        left = left with { FollowingCommands = null }; right = right with { FollowingCommands = null };
        Check(!keys.TryResolve(Key.Left,ModifierKeys.Shift,out _) && !keys.TryResolve(Key.Right,ModifierKeys.Shift,out _), "No implicit Shift arrow binding");
        var settings = SettingsIniReader.Parse("[keys.file]\n>KEY_LEFT ---- &parentleft;&paneleft;\n>KEY_RIGHT ---- &parentright; &paneright; # comment\n");
        var roundtrip = SettingsIniReader.Parse(SettingsIniWriter.Serialize(settings));
        keys.ApplyBindings(roundtrip.KeyBindings);
        Check(keys.TryResolve(Key.Left,ModifierKeys.None,out combinedLeft) && combinedLeft.FollowingCommands?.Single().Command == AppCommand.ActivateLeftPane, "INI left sequence survives save");
        Check(keys.TryResolve(Key.Right,ModifierKeys.None,out combinedRight) && combinedRight.FollowingCommands?.Single().Command == AppCommand.ActivateRightPane, "INI right sequence survives save");
        Check(IniKeyParser.TryParseLine(">KEY_H -s-- &paneleft;&parent;&paneright;", out var triple) && triple!.FollowingCommands?.Length == 2, "Three commands");
        Check(IniKeyParser.TryParseLine(IniKeyParser.FormatLine(triple!), out var tripleSaved) && tripleSaved!.FollowingCommands?.Length == 2, "Three commands survive save");
        foreach (string invalid in new[] { "&parentleft;&unknown;", "&parentleft;;", "&parentleft;garbage", "&parentleft &paneleft;" })
            Check(!IniKeyParser.TryParseLine(">KEY_LEFT ---- " + invalid, out _), "Reject invalid sequence " + invalid);
        var overrides = SettingsIniReader.Parse("[keys.file]\n>KEY_LEFT ---- &parentleft;&paneleft;\n>KEY_LEFT ---- &paneleft;\n");
        keys.ApplyBindings(overrides.KeyBindings);
        Check(keys.TryResolve(Key.Left,ModifierKeys.None,out var single) && single.Command == AppCommand.ActivateLeftPane && single.FollowingCommands == null, "Duplicate key last definition wins");
        Check(IniKeyParser.TryParseLine(">KEY_H ---- &parentleft;",out var binding),"INI accepts custom key");
        keys.ApplyBindings([binding!]);Check(keys.TryResolve(Key.H,ModifierKeys.None,out var custom)&&custom.Command==AppCommand.ParentLeft,"Custom binding resolves");
        Check(IniKeyParser.TryParseLine(IniKeyParser.FormatLine(binding!),out _),"Binding roundtrip");
        Check(IniKeyParser.TryParseLine(">KEY_F3 -s-- &parentleft;&paneleft;", out var functionSequence), "Function sequence parses");
        Check(FunctionKeyBindings.TryResolve([functionSequence!], 3, ModifierKeys.Shift, out var functionCommand) && functionCommand.FollowingCommands?.Single().Command == AppCommand.ActivateLeftPane, "Function button retains sequence");
        var tabSettings = SettingsIniReader.Parse(SettingsIniWriter.Serialize(new MirrorFlux.Models.AppSettings()));
        var tabKeys = new KeyBindingService(); tabKeys.ApplyBindings(tabSettings.KeyBindings);
        Check(tabKeys.TryResolve(Key.Tab, ModifierKeys.None, out var tab) && tab.Command == AppCommand.SwitchPane, "Tab INI default roundtrip");
        Check(tabKeys.TryResolve(Key.Tab, ModifierKeys.Shift, out var shiftTab) && shiftTab.Command == AppCommand.SwitchPane, "Shift Tab INI default roundtrip");
        string root=Path.Combine(Path.GetTempPath(),"mf-pane-"+Guid.NewGuid().ToString("N"));
        string child=Path.Combine(root,"child");Directory.CreateDirectory(child);File.WriteAllText(Path.Combine(child,"file.txt"),"test");
        try
        {
            var vm=new MainViewModel(new SettingsStore(Path.Combine(root,"mf.ini")));
            await vm.LeftPane.LoadAsync(child);await vm.RightPane.LoadAsync(child);vm.ActivateLeftPane();
            await vm.ExecuteAsync(tab);Check(vm.ActivePane == vm.RightPane && vm.RightPane.CurrentPath == child, "Tab switches right without navigation");
            await vm.ExecuteAsync(shiftTab);Check(vm.ActivePane == vm.LeftPane && vm.LeftPane.CurrentPath == child, "Shift Tab switches left without navigation");
            vm.LeftPane.SelectedItem=vm.LeftPane.Entries.First(e=>!e.IsParent);
            await vm.ExecuteAsync(right);Check(vm.ActivePane==vm.LeftPane&&vm.LeftPane.CurrentPath==child,"ParentRight does nothing in left pane");
            Check(IniKeyParser.TryParseCommand("paneright",out var switchRight),"Parse right switch");
            await vm.ExecuteAsync(new(switchRight));Check(vm.ActivePane==vm.RightPane&&vm.RightPane.CurrentPath==child,"Separate right switch preserves path");
            vm.RightPane.SelectedItem=vm.RightPane.Entries.First(e=>!e.IsParent);
            await vm.ExecuteAsync(right);Check(vm.ActivePane==vm.RightPane&&vm.RightPane.CurrentPath==root,"Outward right goes up from file selection");
            await vm.ExecuteAsync(left);Check(vm.ActivePane==vm.RightPane&&vm.RightPane.CurrentPath==root,"ParentLeft does nothing in right pane");
            Check(IniKeyParser.TryParseCommand("paneleft",out var switchLeft),"Parse left switch");
            await vm.ExecuteAsync(new(switchLeft));Check(vm.ActivePane==vm.LeftPane&&vm.LeftPane.CurrentPath==child,"Separate left switch preserves path");
            await vm.ExecuteAsync(left);Check(vm.LeftPane.CurrentPath==root,"Outward left goes up from file selection");
            await vm.LeftPane.LoadAsync(child);await vm.RightPane.LoadAsync(child);vm.ActivateLeftPane();
            await vm.ExecuteAsync(combinedRight);
            Check(vm.ActivePane == vm.RightPane && vm.RightPane.CurrentPath == child, "Combined inward right switches only");
            await vm.ExecuteAsync(combinedRight);
            Check(vm.ActivePane == vm.RightPane && vm.RightPane.CurrentPath == root, "Combined outward right goes up");
            await vm.ExecuteAsync(combinedLeft);
            Check(vm.ActivePane == vm.LeftPane && vm.LeftPane.CurrentPath == child, "Combined inward left switches only");
            await vm.ExecuteAsync(combinedLeft);
            Check(vm.ActivePane == vm.LeftPane && vm.LeftPane.CurrentPath == root, "Combined outward left goes up");
            keys.ApplyBindings([triple!]);keys.TryResolve(Key.H, ModifierKeys.Shift, out var sequence);
            await vm.LeftPane.LoadAsync(child);vm.ActivateRightPane();await vm.ExecuteAsync(sequence);
            Check(vm.LeftPane.CurrentPath == root && vm.ActivePane == vm.RightPane, "Three commands execute in order");
            vm.ActivateLeftPane();
            string zip=Path.Combine(root,"test.zip");using(var z=ZipFile.Open(zip,ZipArchiveMode.Create)){using var w=new StreamWriter(z.CreateEntry("nested/file.txt").Open());w.Write("test");}
            await vm.LeftPane.LoadAsync(zip);var nested=vm.LeftPane.Entries.First(e=>e.IsDirectory&&!e.IsParent);await vm.LeftPane.LoadAsync(nested.FullPath);
            vm.LeftPane.SelectedItem=vm.LeftPane.Entries.First(e=>!e.IsParent);await vm.ExecuteAsync(left);
            Check(vm.LeftPane.Entries.Any(e=>e.Name=="nested"),"Archive child returns to archive root");
            await vm.ExecuteAsync(left);Check(vm.LeftPane.CurrentPath==root&&vm.LeftPane.SelectedItem?.FullPath==zip,"Archive exit restores archive selection");
            string drive=Path.GetPathRoot(root)!;await vm.LeftPane.LoadAsync(drive);await vm.ExecuteAsync(left);Check(vm.LeftPane.CurrentPath==drive,"Drive root stays put");
        }
        finally { Directory.Delete(root,true); }
    }
}
