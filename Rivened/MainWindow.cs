using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gtk;
using UI = Gtk.Builder.ObjectAttribute;

namespace Rivened {
	class MainWindow: Window {
		[UI] private readonly Label lbl_status = null;
		[UI] private readonly FileChooserButton btn_load_game = null;
		[UI] private readonly Button btn_save = null;
		[UI] private readonly Button btn_fetch = null;
		[UI] private readonly Dialog dlg_sheets = null;
		[UI] private readonly Entry dlg_sheets_txt_url = null;
		[UI] private readonly Button dlg_sheets_btn_ok = null;
		[UI] private readonly Button dlg_sheets_btn_cancel = null;
		[UI] private readonly Button btn_revert = null;
		[UI] private readonly Button btn_export = null;
		[UI] private readonly Image img_is_loaded = null;
		[UI] private readonly Box box_toolbar = null;
		[UI] private readonly Box box_script_bar = null;
		[UI] private readonly Button btn_prepare_scripts = null;
		[UI] private readonly CheckButton chk_en_tweaks = null;
		public bool EnTweaks => chk_en_tweaks.Active;
		[UI] private readonly ComboBox cmb_dst_encoding = null;
		public bool UseBig5 => cmb_dst_encoding.Active == 1;
		[UI] private readonly ListBox lst_scripts = null;
		[UI] private readonly Viewport textbox_viewport;
		private readonly TextView txt_textbox = null;
		private bool editingTextbox = false;
		private volatile static bool loadingScript = false;
		public static readonly string INI = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rivened", "rivened.ini");
		private readonly Dictionary<string, string> settings = new Dictionary<string, string>();
		private static readonly Regex SheetsIdRegex = new Regex("^([A-Za-z0-9\\-_]){44}$");
		private string sheetsId;
		public static MainWindow Instance;
		public MainWindow(): this(new Builder("MainWindow.glade")) {}
		
		private async Task<HttpResponseMessage> DownloadSheet(HttpClient client, AFS.Entry entry, Dictionary<string, string> results) {
			for(int i = 1;; i++) {
				var response = await client.GetAsync("https://docs.google.com/spreadsheets/d/" + sheetsId + "/gviz/tq?tqx=out:csv&sheet=" + entry.Name).ConfigureAwait(false);
				if(response.IsSuccessStatusCode) {
					var reading = await response.Content.ReadAsStringAsync();
					results[entry.Name] = reading;
					//Console.WriteLine("Done " + entry.Name);
				} else if(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
					Console.WriteLine("Retrying (" + i + ") " + entry.Name);
					//await Task.Delay(20);
					continue;
				} else {
					Console.WriteLine("Fetching failed for " + entry.Name + " with " + response.StatusCode);
				}
				return response;
			}
		}

		private MainWindow(Builder builder): base(builder.GetRawOwnedObject("MainWindow")) {
			builder.Autoconnect(this);
			Instance = this;
			txt_textbox = new TextView();
			textbox_viewport.Add(txt_textbox);
			txt_textbox.Show();

			DeleteEvent += Window_DeleteEvent;
			btn_load_game.SelectionChanged += (sender, args) => {
				if(btn_load_game.File == null) {
					LoadedGame.Instance = null;
					UpdateState();
				} else {
					var path = btn_load_game.File;
					if(LoadedGame.Load(path)) {
						SetSetting("game_path", path.Path);
						UpdateState("12Riven loaded successfully");
					} else {
						UpdateState("12Riven failed to load");
					}
				}
			};
			btn_prepare_scripts.Clicked += (sender, args) => {
				if(LoadedGame.Instance == null) return;
				if(LoadedGame.Instance.ScriptsPrepared) return;
				LoadedGame.Instance.PrepareScripts();
				UpdateState("12Riven scripts prepared successfully");
			};
			btn_revert.Clicked += (sender, args) => {
				if(LoadedGame.Instance == null) return;
				if(!LoadedGame.Instance.ScriptsPrepared) return;
				if(LoadedGame.Instance.RevertScripts()) {
					UpdateState("Scripts reverted successfully");
				} else {
					Program.Log("Failed to revert scripts, backup may have been deleted");
				}
			};
			btn_export.Clicked += (sender, args) => {
				if(LoadedGame.Instance == null) return;
				if(!LoadedGame.Instance.ScriptsPrepared) return;
				foreach(var entry in LoadedGame.Instance.ScriptAFS.Entries) {
					if(LoadedGame.IsSpecial(entry.Name) || !LoadedGame.ShouldIgnore(entry.Name)) {
						var decomp = LoadedGame.Instance.decompiler.Decompile(LoadedGame.Instance.ScriptAFS, entry);
						if(decomp != null) {
							using var file = LoadedGame.Instance.Path.ResolveRelativePath("FILE/SCENE00TXT/" + entry.Name + ".txt").Create(GLib.FileCreateFlags.ReplaceDestination, null);
							var data = Encoding.UTF8.GetBytes(decomp);
							file.WriteAll(data, (ulong) data.LongLength, out var bytesWritten, null);
						} else {
							Program.Log("Failed to export scripts");
						}
					}
				}
				Program.Log("Scripts exported successfully");
			};
			lst_scripts.RowSelected += (sender, args) => {
				if(loadingScript) return;
				loadingScript = true;
				if(args.Row == null) {
					editingTextbox = true;
					txt_textbox.Buffer.Text = "";
					editingTextbox = false;
					txt_textbox.Editable = false;
					UpdateState();
				} else {
					RefreshScriptView();
				}
				loadingScript = false;
			};
			txt_textbox.Buffer.Changed += (sender, args) => {
				if(!editingTextbox && lst_scripts.SelectedRow != null) {
					LoadedGame.Instance.decompiler.ChangeDump(((Label) lst_scripts.SelectedRow.Child).Text, txt_textbox.Buffer.Text);
				}
			};
			btn_save.Clicked += (sender, args) => {
				Program.Log("Saving...");
				if(LoadedGame.Instance.SaveScripts()) {
					Program.Log("Saved successfully");
				} // if failed, it was already logged by SaveScripts
			};
			chk_en_tweaks.Active = true;
			chk_en_tweaks.Toggled += (sender, args) => {
				SetSetting("en_tweaks", chk_en_tweaks.Active? "true": "false");
			};
			cmb_dst_encoding.Active = 1;
			cmb_dst_encoding.Changed += (sender, args) => {
				SetSetting("dst_encoding", cmb_dst_encoding.Active == 1? "big5": "sjis");
			};
			btn_fetch.Clicked += (sender, args) => {
				dlg_sheets_txt_url.Buffer.Text = sheetsId;
				if(sheetsId != null && sheetsId != "") {
					dlg_sheets_btn_ok.GrabFocus();
				}
				dlg_sheets.Run();
			};
			dlg_sheets_btn_cancel.Clicked += (sender, args) => {
				dlg_sheets.Hide();
			};
			dlg_sheets_btn_ok.Clicked += (sender, args) => {
				string newSheetsId;
				if(dlg_sheets_txt_url.Text.StartsWith("https://")) {
					newSheetsId = dlg_sheets_txt_url.Text.Split('/')[5];
				} else if(dlg_sheets_txt_url.Text.StartsWith("docs.")) {
					newSheetsId = dlg_sheets_txt_url.Text.Split('/')[3];
				} else {
					newSheetsId = dlg_sheets_txt_url.Text;
				}
				if(newSheetsId == null || !SheetsIdRegex.IsMatch(newSheetsId)) {
					Program.Log("Invalid Sheets ID");
					return;
				}
				sheetsId = newSheetsId;
				var results = new Dictionary<string, string>();
				var client = new HttpClient();
				var entries = new List<AFS.Entry>();
				foreach(var entry in LoadedGame.Instance.ScriptAFS.Entries) {
					if(LoadedGame.IsSpecial(entry.Name) || !LoadedGame.ShouldIgnore(entry.Name)) {
						entries.Add(entry);
					}
				}
				var stopwatch = Stopwatch.StartNew();
				// the decompilations will be needed, but decompiling everything actually takes quite a bit; this way, it finishes while downloads are ongoing
				var decompileThread = new Thread(() => {
					for(int i = 0; i < entries.Count; i++) {
						var entry = entries[i];
						LoadedGame.Instance.decompiler.Decompile(LoadedGame.Instance.ScriptAFS, entry);
					}
					Console.WriteLine($"Finished decompiling {LoadedGame.Instance.decompiler.DumpCount} scripts in {stopwatch.ElapsedMilliseconds / 1000.0} seconds");
				});
				decompileThread.Start();
				/*var urls1 = new List<Task>(); // Parallel version, has a bug that makes some have to retry later
				var urls2 = new List<Task>();
				for(int i = 0; i < entries.Count; i += 80) {
					for(int j = i; j < i + 2 && j < entries.Count; j++) {
						urls1.Add(DownloadSheet(client, entries[j], results));
					}
					for(int j = i + 2; j < i + 72 && j < entries.Count; j++) {
						urls2.Add(DownloadSheet(client, entries[j], results));
					}
					Task.WaitAll(urls1.ToArray());
					for(int j = i + 72; j < i + 80 && j < entries.Count; j++) {
						urls2.Add(DownloadSheet(client, entries[j], results));
					}
					Task.WaitAll(urls2.ToArray());
					urls1.Clear();
					urls2.Clear();
				}*/
				var dlThread = new Thread(() => { // Weird threaded version
					var urls1 = new List<Task>();
					var c = entries.Count;
					for(int i = 0; i < c; i += 80) {
						for(int j = i + 0; j < i + 25 && j < c; j++) {
							urls1.Add(DownloadSheet(client, entries[j], results));
						}
						Task.WaitAll(urls1.ToArray());
						urls1.Clear();
					}
				});
				var dlThread2 = new Thread(() => {
					var urls1 = new List<Task>();
					var c = entries.Count;
					for(int i = 0; i < c; i += 80) {
						for(int j = i + 25; j < i + 50 && j < c; j++) {
							urls1.Add(DownloadSheet(client, entries[j], results));
						}
						Task.WaitAll(urls1.ToArray());
						urls1.Clear();
					}
				});
				dlThread.Start();
				dlThread2.Start();
				var urls1 = new List<Task>();
				var c = entries.Count;
				for(int i = 0; i < c; i += 80) {
					for(int j = 50; j < i + 80 && j < c; j++) {
						urls1.Add(DownloadSheet(client, entries[j], results));
					}
					Task.WaitAll(urls1.ToArray());
					urls1.Clear();
				}
				dlThread.Join();
				dlThread2.Join();
				/*var urls1 = new List<Task>(); // Simple version
				var c = entries.Count;
				for(int i = 0; i < c; i += 80) {
					for(int j = i; j < i + 80 && j < c; j++) {
						urls1.Add(DownloadSheet(client, entries[j], results));
					}
					Task.WaitAll(urls1.ToArray());
					urls1.Clear();
				}*/
				decompileThread.Join();
				foreach(var entry in entries) {
					if(!results.ContainsKey(entry.Name)) {
						Program.Log("Having to retry " + entry.Name + " due to some bug");
						var task = DownloadSheet(client, entry, results);
						task.Wait();
					}
					var csv = results[entry.Name];
					var csvChoices = new List<string[]>();
					var csvLines = new Dictionary<int, string>();
					var csvDatas = new List<string>();
					var csvDatasPostTitle = new List<string>();
					var patches = new List<string>();
					var titlesReached = false;
					var curPatch = null as string;
					var idOffset = 0;
					foreach(var line_ in csv.Split('\n')) {
						var line = line_;
						if(line.Length == 0) continue;
						bool hasQuote = line[0] == '"';
						int id = 0;
						if(entry.Name != "DATA.BIN") {
							for(int j = hasQuote? 1: 0; j < line.Length; j++) {
								if(char.IsDigit(line[j])) {
									id = id * 10 + (line[j] - '0');
								} else {
									break;
								}
							}
							if(id != 0) {
								id -= idOffset;
							}
						}
						int jpLineEnd;
						int start;
						if(hasQuote) {
							start = line.IndexOf('"', 1);
							while(start != -1 && start + 1 < line.Length && line[start + 1] == '"') {
								start = line.IndexOf('"', start + 2);
							}
							if(start == -1) continue;
							jpLineEnd = start;
							start = line.IndexOf(',', start + 1);
						} else {
							start = line.IndexOf(',');
							jpLineEnd = start;
						}
						if(start == -1) continue;
						var jpLine = line[..jpLineEnd];
						var isChoice = false;
						if(entry.Name != "DATA.BIN") {
							if(jpLine.EndsWith('/') && jpLine.IndexOf('/') != jpLine.LastIndexOf('/')) {
								idOffset++;
								//continue;
								isChoice = true;
							}
						}
						hasQuote = line[start + 1] == '"';
						start += hasQuote? 2: 1;
						int end;
						if(hasQuote) {
							end = line.IndexOf('"', start);
							while(end != -1 && end + 1 < line.Length && line[end + 1] == '"') {
								line = line.Remove(end, 1);
								end = line.IndexOf('"', end + 1);
							}
							Trace.Assert(end != -1);
						} else {
							end = line.IndexOf(',', start + 1);
						}
						var tl = end == -1? line[start..]: line[start..end];
						if(entry.Name != "DATA.BIN") {
							if(tl.StartsWith("!patch:")) {
								if(curPatch != null) patches.Add(curPatch);
								curPatch = tl[6..].Trim();
							} else if(tl.StartsWith("!patch")) {
								if(curPatch != null) {
									curPatch += '\n' + tl[6..].Trim();
								} else {
									curPatch = tl[6..].Trim();
								}
								continue;
							} else if(curPatch != null) {
								patches.Add(curPatch);
								curPatch = null;
							}
							if(id == 0) {
								continue;
							}
						}
						if(line.Length != 0 && line[0] == '"') {
							line = line[1..];
						}
						if(line.StartsWith("Title Name: ")) {
							titlesReached = true;
							csvDatasPostTitle.Add(tl);
						} else if(line.StartsWith("String: ")) {
							if(titlesReached) {
								csvDatasPostTitle.Add(tl);
							} else {
								csvDatas.Add(tl);
							}
						} else if(line.StartsWith("Name: ") || line.StartsWith("Route Name: ") || line.StartsWith("RouteName: ")) {
							csvDatas.Add(tl);
						} else if(isChoice) {
							csvChoices.Add(tl.Split('/'));
						} else if(tl != "" && id != 0) {
							csvLines[id] = tl;
						}
					}
					if(curPatch != null) {
						patches.Add(curPatch);
					}
					var choiceSetIdx = 0;
					var lineIdx = 1;
					var decompiled = LoadedGame.Instance.decompiler.Decompile(LoadedGame.Instance.ScriptAFS, entry);
					var decompLines = decompiled.Split('\n');
					var modified = false;
					titlesReached = false;
					var datasIdx = 0;
					var datasPostTitleIdx = 0;
					for(int j = 0; j < decompLines.Length; j++) {
						var line = decompLines[j];
						var dotIdx = line.IndexOf('.');
						if(dotIdx == -1) continue;
						var lineOp = line[(line.LastIndexOf(' ', dotIdx - 1) + 1)..dotIdx];
						if(lineOp == "name") {
							if(datasIdx < csvDatas.Count) {
								var tl = csvDatas[datasIdx++];
								if(tl != "") {
									decompLines[j] = line[..(line.IndexOf('@') + 1)] + tl;
									modified = true;
								}
							}
						} else if(lineOp == "route" || lineOp == "route2") {
							if(datasIdx < csvDatas.Count) {
								var tl = csvDatas[datasIdx++];
								if(tl != "" && tl.Contains("//")) {
									var tls = tl.Split("//");
									decompLines[j] = line[..(line.IndexOf('§') + 1)] + tls[0].Trim() + "§ @" + tls[1].Trim();
									modified = true;
								}
							}
						} else if(lineOp == "string") {
							if(titlesReached? datasPostTitleIdx < csvDatasPostTitle.Count: datasIdx < csvDatas.Count) {
								var tl = titlesReached? csvDatasPostTitle[datasPostTitleIdx++]: csvDatas[datasIdx++];
								if(tl != "") {
									decompLines[j] = line[..(line.IndexOf('@') + 1)] + tl;
									modified = true;
								}
							}
						} else if(lineOp == "title") {
							titlesReached = true;
							if(datasPostTitleIdx < csvDatasPostTitle.Count) {
								var tl = csvDatasPostTitle[datasPostTitleIdx++];
								if(tl != "") {
									decompLines[j] = line[..(line.IndexOf('@') + 1)] + tl;
									modified = true;
								}
							}
						} else if(lineOp == "select") {
							var tls = csvChoices[choiceSetIdx++];
							if(tls.Length > 1) {
								int tlIdx = 0;
								int strIdx = 0;
								string tl;
								while((strIdx = line.IndexOf('§', strIdx + 1)) != -1) {
									int start = strIdx + 1;
									int end = line.IndexOf('§', start);
									Trace.Assert(end != -1);
									tl = tls[tlIdx].Trim();
									if(tl != "") {
										line = line[..start] + tl + line[end..];
									}
									strIdx = line.IndexOf('§', start); // must be redone due to the reconstruction above
									tlIdx++;
								}
								tl = tls[tlIdx].Trim();
								if(tl != "") {
									line = line[..(line.IndexOf('@', strIdx + 1) + 1)] + tl;
								}
								decompLines[j] = line;
								modified = true;
							}
						} else if(lineOp == "message") {
							int strMarker = line.IndexOf('@');
							if(csvLines.TryGetValue(lineIdx, out var replacement)) {
								var terminatorReplaceIdx = replacement.IndexOf("%R");
								string term = null;
								if(terminatorReplaceIdx != -1) {
									term = replacement[(terminatorReplaceIdx + 2)..];
									replacement = replacement[..terminatorReplaceIdx];
								}
								if(line[strMarker + 1] == '【') {
									var startQuote = line.IndexOf('「', strMarker + 2) + 1;
									if(startQuote != 0) {
										var endQuote = line.LastIndexOf('」', startQuote + 1);
										if(endQuote == -1) {
											if(term == null) {
												// for some reason, KID has quite the tendency to just forget end quotes, though in 12R it's only some email lines
												int terminatorStart = line.Length - 1;
												while(terminatorStart > strMarker + 1 && line[terminatorStart - 1] == '%' && line[terminatorStart] != '%') {
													terminatorStart -= 2;
												}
												term = line[(terminatorStart + 1)..];
											}
											decompLines[j] = line[..startQuote] + replacement + '」' + term;
										} else {
											term ??= line[endQuote..];
											decompLines[j] = line[..startQuote] + replacement + term;
										}
									}
								} else {
									if(term == null) {
										int terminatorStart = line.Length - 1;
										while(terminatorStart > strMarker + 1 && line[terminatorStart - 1] == '%' && line[terminatorStart] != '%') {
											terminatorStart -= 2;
										}
										term = line[(terminatorStart + 1)..];
									}
									decompLines[j] = line[..(strMarker + 1)] + replacement + term;
								}
								modified = true;
							}
							lineIdx++;
						}
					}
					if(modified || patches.Count != 0) { // the modified flag might not be necessary
						var finalized = string.Join('\n', decompLines);
						if(patches.Count != 0) {
							(var ok, var txt) = ScriptCompiler.ApplyPatches(entry.Name, finalized, patches.ToArray());
							if(ok) {
								Program.Log(entry.Name + ": success applying patches");
								finalized = txt;
							} else {
								Program.Log(entry.Name + ": error applying patch " + txt);
								return;
							}
						}
						LoadedGame.Instance.decompiler.ChangeDump(entry.Name, finalized);
						RefreshScriptView();
					}
				}
				stopwatch.Stop();
				dlg_sheets.Hide();
				SetSetting("sheets_id", sheetsId);
				Program.Log($"Finished importing {results.Count} sheets in {stopwatch.ElapsedMilliseconds / 1000.0} seconds");
			};

			if(File.Exists(INI)) {
				foreach(string line in File.ReadAllText(INI).Split("\n")) {
					int index = line.IndexOf("=");
					if(index != -1) {
						settings[line[..index].Trim()] = line[(index + 1)..].Trim();
					}
				}
				if(settings.TryGetValue("sheets_id", out var sheets_id)) {
					sheetsId = sheets_id;
				}
				if(settings.TryGetValue("en_tweaks", out var en_tweaks)) {
					if(en_tweaks == "false") {
						chk_en_tweaks.Active = false;
					}
				}
				if(settings.TryGetValue("dst_encoding", out var dst_encoding)) {
					if(dst_encoding == "sjis") {
						cmb_dst_encoding.Active = 0;
					}
				}
				if(settings.TryGetValue("game_path", out var path)) {
					var gpath = GLib.FileFactory.NewForPath(path);
					btn_load_game.SetFile(gpath);
					if(LoadedGame.Load(gpath)) {
						UpdateState("12Riven loaded successfully");
					} else {
						UpdateState("12Riven failed to load");
					}
					return;
				}
			}
			UpdateState();
		}

		public void UpdateLog() {
			lbl_status.Text = Program.LatestLog;
		}

		public void RefreshScriptView() {
			txt_textbox.Editable = true;
			if(lst_scripts.SelectedRow == null) {
				txt_textbox.Buffer.Text = "";
			}
			txt_textbox.Editable = lst_scripts.SelectedRow != null;
			if(lst_scripts.SelectedRow != null) {
				string lookingFor = ((Label) lst_scripts.SelectedRow.Child).Text;
				AFS.Entry entry = null;
				foreach(var e in LoadedGame.Instance.ScriptAFS.Entries) {
					if(e.Name.Replace("_", "") == lookingFor) {
						entry = e;
						break;
					}
				}
				if(entry == null) {
					throw new Exception("Could not find " + lookingFor);
				}
				editingTextbox = true;
				txt_textbox.Buffer.Text = LoadedGame.Instance.decompiler.Decompile(LoadedGame.Instance.ScriptAFS, entry);
				editingTextbox = false;
				UpdateState();
			}
		}

		public void UpdateState(string withLog = "") {
			Application.Invoke((sender, args) => {
				if(withLog != "") {
					Program.Log(withLog);
				}
				if(lst_scripts.SelectedRow != null) {
					txt_textbox.Editable = true;
				}
				img_is_loaded.Stock = LoadedGame.Instance != null? "gtk-yes": "gtk-no";
				btn_prepare_scripts.Visible = LoadedGame.Instance != null && !LoadedGame.Instance.ScriptsPrepared;
				box_script_bar.Visible = LoadedGame.Instance != null && LoadedGame.Instance.ScriptsPrepared;
				if(LoadedGame.Instance == null || !LoadedGame.Instance.ScriptsPrepared) {
					lst_scripts.UnselectAll();
					foreach(var child in lst_scripts.Children) {
						lst_scripts.Remove(child);
					}
				} else if(LoadedGame.Instance.ScriptListDirty) {
					lst_scripts.UnselectAll();
					foreach(var child in lst_scripts.Children) {
						lst_scripts.Remove(child);
					}
					if(LoadedGame.Instance.ScriptsPrepared) {
						foreach(var entry in LoadedGame.Instance.ScriptAFS.Entries) {
							if(LoadedGame.IsSpecial(entry.Name) || !LoadedGame.ShouldIgnore(entry.Name)) {
								lst_scripts.Add(new Label(entry.Name) {
									WidthRequest = 120,
									Justify = Justification.Left
								});
							}
						}
						lst_scripts.ShowAll();
					}
					LoadedGame.Instance.ScriptListDirty = false;
				}
				if(lst_scripts.SelectedRow == null) {
					txt_textbox.Editable = false;
				}
			});
		}

		public void SetSetting(string key, string value) {
			settings[key] = value;
			string rebuilt = "";
			foreach(var pair in settings) {
				rebuilt += pair.Key + '=' + pair.Value + '\n';
			}
			Directory.CreateDirectory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rivened"));
			File.WriteAllText(INI, rebuilt);
		}

		private void Window_DeleteEvent(object sender, DeleteEventArgs a) {
		   	Gtk.Application.Quit();
		}
	}
}
