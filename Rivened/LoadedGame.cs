using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using IFile = GLib.IFile;

namespace Rivened {
	public class LoadedGame {
		public static LoadedGame Instance;

		public static bool Load(IFile path) {
			Instance = null;
			if((path.ResolveRelativePath("FILE/SCENE00.afs")?.Exists == true ||
					path.ResolveRelativePath("FILE/SCENE00.afs.bak")?.Exists == true) &&
					path.ResolveRelativePath("FILE/FONTS_PC.AFS")?.Exists == true &&
					path.ResolveRelativePath("FILE/BGL/BGL00_PC.AFS")?.Exists == true) {
				Instance = new LoadedGame(path);
				return true;
			}
			return false;
		}

		public static bool IsSpecial(string name) {
			return name.StartsWith("DATA");
		}

		public static bool ShouldIgnore(string name) {
			return name.StartsWith("DBG")
				|| name.StartsWith("MAIN")
				|| name.StartsWith("DMENU")
				|| name.StartsWith("SHORTCUT")
				|| name.StartsWith("INIT")
				|| name.StartsWith("CLRFLG")
				|| name.StartsWith("DICT")
				|| name.StartsWith("DATA");
		}

		public ScriptDecompiler decompiler = new ScriptDecompiler();
		public IFile Path;
		public bool ScriptsPrepared = false;
		public bool ScriptListDirty = false;
		public AFS ScriptAFS;
		public FontSizeData FontSizeData = null;

		public LoadedGame(IFile path) {
			Path = path;
			if(Path.ResolveRelativePath("FILE/SCENE00.afs")?.Exists == true &&
					Path.ResolveRelativePath("FILE/SCENE00.afs.bak")?.Exists == true) {
				Trace.Assert(LoadScripts());
			}
		}

		public bool PrepareScripts() {
			if(Path.ResolveRelativePath("FILE/SCENE00.afs")?.Exists == true &&
					Path.ResolveRelativePath("FILE/SCENE00.afs.bak")?.Exists == true) {
				return LoadScripts();
			}
			var sceneBackup = Path.ResolveRelativePath("FILE/SCENE00.afs.bak");
			var scene = Path.ResolveRelativePath("FILE/SCENE00.afs");
			if(!sceneBackup.Exists) {
				scene.Copy(sceneBackup, GLib.FileCopyFlags.AllMetadata, null, null);
			}
			if(!scene.Exists) {
				sceneBackup.Copy(scene, GLib.FileCopyFlags.AllMetadata, null, null);
			}
			return LoadScripts();
		}

		public bool RevertScripts() {
			var sceneBackup = Path.ResolveRelativePath("FILE/SCENE00.afs.bak");
			var scene = Path.ResolveRelativePath("FILE/SCENE00.afs");
			if(Path.ResolveRelativePath("FILE/SCENE00.afs.bak")?.Exists == true) {
				sceneBackup.Copy(scene, GLib.FileCopyFlags.Overwrite | GLib.FileCopyFlags.AllMetadata, null, null);
				decompiler = new ScriptDecompiler();
				ScriptAFS = new AFS(Path.ResolveRelativePath("FILE/SCENE00.afs"));
				FontSizeData ??= new FontSizeData(Path.ResolveRelativePath("FILE/FONTS_PC.AFS"));
				ScriptListDirty = true;
				return true;
			}
			return false;
		}

		public bool LoadScripts() {
			ScriptsPrepared = true;
			ScriptAFS = new AFS(Path.ResolveRelativePath("FILE/SCENE00.afs"));
			FontSizeData ??= new FontSizeData(Path.ResolveRelativePath("FILE/FONTS_PC.AFS"));
			ScriptListDirty = true;
			return true;
		}

		public bool SaveScripts() {
			var tasks = new List<Task>();
			string lastErr = null;
			var isFirst = true;
			foreach(var entry in ScriptAFS.Entries) {
				entry.Load(ScriptAFS);
				if((IsSpecial(entry.Name) || !ShouldIgnore(entry.Name)) && decompiler.CheckAndClearModified(entry.Name)) {
					var fn = () => {
						if(ScriptCompiler.Compile(entry.Name, decompiler.Decompile(ScriptAFS, entry), out var arr, out var err)) {
							Trace.Assert(arr.Length != 0);
							Trace.Assert(arr[0] != 0);
							entry.SetData(arr);
						} else {
							err = entry.Name + ':' + err;
							Program.Log(err);
							lastErr = err;
							return;
						}
					};
					if(isFirst) {
						fn();
						isFirst = false;
					} else {
						tasks.Add(Task.Factory.StartNew(fn));
					}
				}
			}
			Task.WaitAll(tasks.ToArray());
			if(lastErr != null) {
				Program.LatestLog = lastErr;
				MainWindow.Instance.UpdateState();
				return false;
			}
			ScriptAFS.Save();
			return true;
		}
	}
}