using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Rivened {
	public class ScriptDecompiler {
		private volatile bool UseBig5 = true;
		private volatile Dictionary<string, string> Dumps = new Dictionary<string, string>();
		private HashSet<string> Modified = new HashSet<string>();
		public int DumpCount => Dumps.Count;

		public bool CheckAndClearModified(string filename) {
			if(Modified.Contains(filename)) {
				Modified.Remove(filename);
				return true;
			}
			// the user needs to be able to just reencode, so it turns out we do always need to decompile
			return true;
		}

		public void ChangeDump(string filename, string data) {
			Debug.Assert(Dumps.ContainsKey(filename));
			Dumps[filename] = data;
			Modified.Add(filename);
		}

		public string Decompile(AFS afs, AFS.Entry entry) {
			if(Dumps.TryGetValue(entry.Name, out var dump)) {
				return dump;
			}
			return Decompile(entry.Name, entry.Load(afs));
		}

		public string Decompile(string filename, byte[] bytes) {
			if(Dumps.TryGetValue(filename, out var dump)) {
				return dump;
			}
			if(filename.StartsWith("DATA")) {
				return DecompileData(filename, bytes);
			}
			bool retried = false;
			retry:
			var res = "header." + bytes[0] + ' ' + BitConverter.ToString(bytes[1..bytes[0]]);
			int pos = bytes[0];
			var endOfMessages = 0;
			while(true) {
				byte op = bytes[pos];
				byte len = bytes[pos + 1];
				pos += 2;
				if(op == (byte) TokenType.message) {
					try {
						res += DumpCmdWithStrings(bytes, ref endOfMessages, "message", len - 2, true, pos, 4);
					} catch {
						if(retried) {
							throw new Exception("could not decode script");
						}
						retried = true;
						UseBig5 = !UseBig5;
						goto retry;
					}
				} else if(op == (byte) TokenType.select) {
					int count = len / 8 - 1;
					var choices = new int[count];
					for(int i = 0; i < count; i++) {
						choices[i] = i * 8 + 6;
					}
					try {
						res += DumpCmdWithStrings(bytes, ref endOfMessages, "select", len - 2, true, pos, choices);
					} catch {
						if(retried) {
							throw new Exception("could not decode script");
						}
						retried = true;
						UseBig5 = !UseBig5;
						goto retry;
					}
				} else if(op == (byte) TokenType.select2) {
					try {
						res += DumpCmdWithStrings(bytes, ref endOfMessages, "select2", len - 2, true, pos, 4);
					} catch {
						if(retried) {
							throw new Exception("could not decode script");
						}
						retried = true;
						UseBig5 = !UseBig5;
						goto retry;
					}
				} else if(len > 2) {
					if(pos + len - 2 > bytes.Length) {
						throw new Exception("OOB " + pos + ".." + (pos + len - 2) + " in script of len " + bytes.Length);
					}
					res += '\n' + ((TokenType) op).ToString() + '.' + len + ' ' + BitConverter.ToString(bytes[pos..(pos + len - 2)]);
				} else if(len == 2) {
					res += '\n' + ((TokenType) op).ToString() + '.' + len;
				}
				pos += len - 2;
				if(pos > endOfMessages) endOfMessages = pos;
				if(op == 0x0D) {
					break;
				}
			}
			res += "\ntrailer." + (bytes.Length - endOfMessages) + ' ' + BitConverter.ToString(bytes[endOfMessages..]);
			Dumps[filename] = res;
			return res;
		}

		public class DataSectionInfo {
			public string Name;
			public int SizeJp;
			public int SizeDiffCn = 0;
			public int ItemLen;
			public DataSectionInfo(string name, int size, int itemLen) {
				Name = name;
				SizeJp = size;
				ItemLen = itemLen;
			}
		}
		public static readonly Dictionary<string, string> SECTIONS_SHEETS = new() {
			["Name: "] = "name",
			["Route Name: "] = "route",
			["RouteName: "] = "route2",
			["String: "] = "string",
			["Title Name: "] = "title",
		};
		public static readonly DataSectionInfo[] SECTIONS = new DataSectionInfo[] {
			new DataSectionInfo("name", 0x830, 16),
			new DataSectionInfo("route", 0x630, 24),
			new DataSectionInfo("route2", 0x60, 24),
			new DataSectionInfo("unk", 0x59C, 0x59C),
			new DataSectionInfo("scene", 0x2D8, 8),
			new DataSectionInfo("unk2", 0x6A4, 0x6A4),
			new DataSectionInfo("string", 0x60C, 4) {SizeDiffCn = 0x10},
			new DataSectionInfo("chunk", 0x4, 4),
			new DataSectionInfo("title", 0xF0, 8),
			new DataSectionInfo("chunk", 0x4, 4),
			new DataSectionInfo("string", 0xE4, 4),
			new DataSectionInfo("chunk", 0x4, 4),
			new DataSectionInfo("string", 0x8, 4),
			new DataSectionInfo("footer", 0x14, 0x14)
		};

		private string DumpCmdWithStrings(byte[] bytes, ref int endOfStrings, string name, int len, bool isScriptCmd, int dataPos, params int[] strsPos) {
			string res = '\n' + name + '.' + (isScriptCmd? len + 2: len) + ' ';
			if(strsPos[0] != 0) {
				res += BitConverter.ToString(bytes[dataPos..(dataPos + strsPos[0])]) + '-';
			}
			var strs = new string[strsPos.Length];
			for(int i = 0; i < strsPos.Length; i++) {
				if(i + 1 == strsPos.Length) {
					if(strsPos[i] + 2 < len) {
						res += "S-" + BitConverter.ToString(bytes[(dataPos + strsPos[i] + 2)..(dataPos + len)]);
					} else {
						res += "S";
					}
				} else {
					res += "S-" + BitConverter.ToString(bytes[(dataPos + strsPos[i] + 2)..(dataPos + strsPos[i + 1])]) + '-';
				}
				var strStart = BitConverter.ToUInt16(bytes, dataPos + strsPos[i]);
				int strEnd = strStart;
				while(bytes[strEnd] != 0) {
					strEnd++;
				}
				if(strEnd + 1 > endOfStrings) {
					endOfStrings = strEnd + 1;
				}
				strs[i] = UseBig5? Big5.Decode(bytes[strStart..strEnd]):
				 	Program.SJIS.GetString(bytes.AsSpan(strStart..strEnd));
			}
			for(int i = 0; i < strs.Length; i++) {
				if(i + 1 == strs.Length) {
					res += " @" + strs[i];
				} else {
					res += " §" + strs[i] + '§';
				}
			}
			return res;
		}

		private string DecompileData(string filename, byte[] bytes) {
			if(Dumps.TryGetValue(filename, out var dump)) {
				return dump;
			}
			bool retried = false;
			retry:
			var res = "header." + bytes[0] + ' ' + BitConverter.ToString(bytes[1..bytes[0]]);
			int pos = bytes[0];
			int endOfMessages = 0;// = encoding.BodyName == "big5"? 0x5733: 0x5ACD;
			for(int i = 0; i < SECTIONS.Length; i++) {
				var section = SECTIONS[i];
				var endOfSection = pos + section.SizeJp;
				//if(encoding.BodyName == "big5") {
				//	endOfSection += section.SizeDiffCn;
				//}
				while(pos < endOfSection) {
					if(section.Name == "name") {
						try {
							res += DumpCmdWithStrings(bytes, ref endOfMessages, section.Name, section.ItemLen, false, pos, 0, 4);
						} catch {
							if(retried) {
								throw new Exception("could not decode DATA.BIN");
							}
							retried = true;
							UseBig5 = !UseBig5;
							goto retry;
						}
					} else if(section.Name == "route") {
						try {
							res += DumpCmdWithStrings(bytes, ref endOfMessages, section.Name, section.ItemLen, false, pos, 4, 8);
						} catch {
							if(retried) {
								throw new Exception("could not decode DATA.BIN");
							}
							retried = true;
							UseBig5 = !UseBig5;
							goto retry;
						}
					} else if(section.Name == "route2") {
						try {
							res += DumpCmdWithStrings(bytes, ref endOfMessages, section.Name, section.ItemLen, false, pos, 8, 12);
						} catch {
							if(retried) {
								throw new Exception("could not decode DATA.BIN");
							}
							retried = true;
							UseBig5 = !UseBig5;
							goto retry;
						}
					} else if(section.Name == "scene" || section.Name == "string" || section.Name == "title") {
						try {
							res += DumpCmdWithStrings(bytes, ref endOfMessages, section.Name, section.ItemLen, false, pos, 0);
						} catch {
							if(retried) {
								throw new Exception("could not decode DATA.BIN");
							}
							retried = true;
							UseBig5 = !UseBig5;
							goto retry;
						}
					} else {
						res += '\n' + section.Name + '.' + section.ItemLen + ' ' + BitConverter.ToString(bytes[pos..(pos + section.ItemLen)]);
					}
					pos += section.ItemLen;
				}
			}
			res += "\ntrailer." + (bytes.Length - endOfMessages) + ' ' + BitConverter.ToString(bytes[endOfMessages..]);
			Dumps[filename] = res;
			return res;
		}
	}

	public enum TokenType {
		ext_Goto = 0x09,
		ext_Call = 0x0A,
		ext_Goto2 = 0x0B,
		ext_Call2 = 0x0C,
		ret2 = 0x0D,
		thread = 0x0E,
		skip_jump = 0x14,
		key_wait = 0x15,
		message = 0x18,
		windows = 0x1A,
		select = 0x1B,
		selectP = 0x1C,
		select2 = 0x1D,
		popup = 0x1E,
		mes_sync = 0x1F,
		mes_log = 0x24,
		scr_mode = 0x20,
		set_save_point = 0x21,
		clear_save_point = 0x22,
		set_prv_point = 0x23,
		auto_start = 0x25,
		auto_stop = 0x26,
		quick_Save = 0x27,
		mes_log_save = 0xF8,
		title_display = 0x28,
		location_display = 0x2B,
		date_display = 0x29,
		get_options = 0x2C,
		set_icon = 0x2D,
		menu_enable = 0x2E,
		menu_disable = 0x2F,
		fade_out = 0x30,
		fade_in = 0x31,
		fade_out_start = 0x32,
		fade_out_stop = 0x33,
		fade_wait = 0x34,
		fade_pri = 0x36,
		filt_in = 0x38,
		filt_out = 0x39,
		filt_out_start = 0x3B,
		filt_in_start = 0x3A,
		filt_wait = 0x3C,
		filt_pri = 0x3E,
		char_init = 0x40,
		char_display = 0x42,
		char_ers = 0x43,
		char_no = 0x50,
		char_on = 0x51,
		char_pri = 0x52,
		char_animation = 0x53,
		char_sort = 0x54,
		char_swap = 0x55,
		char_shadow = 0x56,
		char_ret = 0x57,
		char_attack = 0x59,
		get_background_c = 0x5F,
		obj_ini = 0x60,
		obj_display = 0x62,
		obj_erase = 0x63,
		obj_no = 0x70,
		obj_on = 0x71,
		obj_pri = 0x72,
		obj_animation = 0x73,
		obj_sort = 0x74,
		obj_swap = 0x75,
		face_ini = 0x80,
		face_display = 0x82,
		face_erase = 0x83,
		face_pos = 0x84,
		face_auto_pis = 0x85,
		face_no = 0x88,
		face_on = 0x89,
		face_pri = 0x8A,
		face_animation = 0x8B,
		face_shadow = 0x8E,
		face_ret = 0x8F,
		bg_init = 0x90,
		bg_display = 0x91,
		bg_erase = 0x92,
		bg_flag = 0x93,
		bg_on = 0x9B,
		bg_pri = 0x9C,
		bg_att = 0x9D,
		bg_bnk = 0x9E,
		bg_swap = 0x9F,
		effect_start = 0xA1,
		effect_par = 0xA2,
		effect_stop = 0xA3,
		sound_effect_play = 0xC8,
		sound_effect_start = 0xC9,
		sound_effect_stop = 0xCA,
		sound_effect_wait = 0xCB,
		sound_effect_vol = 0xCC,
		s_sound_effect_start = 0xCE,
		voice_over_play = 0xD0,
		voice_over_start = 0xD1,
		voice_over_stop = 0xD2,
		voice_over_wait = 0xD3,
		voice_over_sts = 0xD4, 
		moive_play = 0xD8,
		movie_start = 0xD9,
		move_stop_0xDA,
		move_wait = 0xDB,
		key_start = 0xDC,
		vibration_start = 0xE0,
		vibration_stop = 0xE1,
		screen_calen_start = 0xE2,
		screen_calen_end = 0xE3,
		RT = 0xE4,
		print = 0xF0,
		debug_set = 0xF1,
		debug_get = 0xF2,
		title_on = 0xF3,
		dict_set = 0xF4,
		dict_flag = 0xF5,
		message_flag = 0xF6,
		sel_flag = 0xF7,
		set_back_col = 0xF9,
		set_name = 0xFA,
		Tlst_call_on = 0xFB,
		set_thum = 0xFC,
		eot = 0xFE,
		eos = 0xFF,
		event_ini = 0xB0,
		event_release = 0xB1,
		event_open = 0xB3,
		event_close = 0xB4,
		event_load = 0xB2,
		event_key_wait = 0xB5,
		Rain_Effect = 0xA1,
		Loop_Cond = 0x06,
	}
}