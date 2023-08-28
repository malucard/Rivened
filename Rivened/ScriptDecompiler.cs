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
			Trace.Assert(Dumps.ContainsKey(filename));
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
			if(Dumps.TryGetValue(filename, out var cached)) {
				return cached;
			}
			if(filename.StartsWith("DATA")) {
				return DecompileData(filename, bytes);
			}
			var retried = false;
			retry:
			var res = "header." + bytes[0] + ' ' + BitConverter.ToString(bytes, 1, bytes[0] - 1);
			var pos = (int) bytes[0];
			var strsBounds = (0xFFFFFF, 0);
			var offsetsToLines = new Dictionary<uint, int>(); // (binary location, decompiled string position) the first is for the header line
			var labels = new SortedSet<uint>(); // destination position
			try {
				while(true) {
					offsetsToLines[(uint) pos] = res.Length;
					var op = bytes[pos];
					var fullLen = bytes[pos + 1]; // length of opcode + length + data
					if(fullLen == 0) {
						break;
					}
					pos += 2;
					var len = (int) fullLen - 2; // now pos and len are for the data, excluding the opcode and length
					var instDump = '\n' + ((Opcode) op).ToString() + '.' + fullLen;
					if(op == (byte) Opcode.message) {
						instDump += ' ' + BitConverter.ToString(bytes, pos, 4) + "-S-" + BitConverter.ToString(bytes, pos + 6, len - 6) + DumpStrings(bytes, ref strsBounds, pos, 4);
					} else if(op == (byte) Opcode.Loop_Cond) {
						var dst = (uint) bytes[pos] | (uint) bytes[pos + 1] << 8;
						labels.Add(dst);
						instDump += " &L" + dst.ToString("X4") + '-' + BitConverter.ToString(bytes, pos + 2, len - 2);
					} else if(op == (byte) Opcode.select) {
						int count = fullLen / 8 - 1;
						var choices = new int[count];
						instDump += ' ' + BitConverter.ToString(bytes, pos, 6);
						for(int i = 0; i < count; i++) {
							var choicePos = i * 8 + 6;
							instDump += "-S-" + BitConverter.ToString(bytes, pos + choicePos + 2, 6);
							choices[i] = choicePos;
						}
						if(len > count * 8 + 6) {
							instDump += '-' + BitConverter.ToString(bytes, count * 8 + 6, len - count * 8 - 6);
						}
						instDump += DumpStrings(bytes, ref strsBounds, pos, choices);
					} else if(op == (byte) Opcode.select2) { // wait, why? isn't this unused, what even was it? was this branch by mistake?
						instDump += ' ' + DumpCmdWithStrings(bytes, ref strsBounds, len, pos, 4);
					} else if(len > 0) {
						instDump += ' ' + BitConverter.ToString(bytes, pos, len);
					}
					res += instDump;
					pos += len;
					if(pos >= strsBounds.Item1) {
						break;
					}
					//if(op == 0x0D) {
					//	break;
					//}
				}
			} catch(DecoderFallbackException e) {
				if(retried) {
					throw new AggregateException(e, new Exception("could not decode script " + filename + ": " + e.Message + ", from " + e.Source));
				}
				retried = true;
				UseBig5 = !UseBig5;
				goto retry;
			}
			res += "\ntrailer." + (bytes.Length - strsBounds.Item2) + ' ' + BitConverter.ToString(bytes, strsBounds.Item2);
			if(labels.Count != 0) {
				int offset = 1;
				foreach(var location in labels) {
					var labelText = "&L" + location.ToString("X4") + ": ";
					var linePos = offsetsToLines[location] + offset;
					res = res[..linePos] + labelText + res[linePos..];
					offset += labelText.Length;
				}
			}
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

		private string DumpStrings(byte[] bytes, ref (int, int) strsBounds, int dataPos, params int[] strsPos) {
			string res = "";
			for(int i = 0; i < strsPos.Length; i++) {
				var strStart = bytes[dataPos + strsPos[i]] | bytes[dataPos + strsPos[i] + 1] << 8;
				int strEnd = strStart;
				while(bytes[strEnd] != 0) {
					strEnd++;
				}
				if(strStart < strsBounds.Item1) {
					strsBounds.Item1 = strStart;
				}
				if(strEnd + 1 > strsBounds.Item2) {
					strsBounds.Item2 = strEnd + 1;
				}
				var str = UseBig5? Big5.Decode(bytes.AsSpan(strStart..strEnd)):
				 	Program.SJIS.GetString(bytes.AsSpan(strStart..strEnd));
				if(i + 1 == strsPos.Length) {
					res += " @" + str;
				} else {
					res += " §" + str + '§';
				}
			}
			return res;
		}

		private string DumpCmdWithStrings(byte[] bytes, ref (int, int) strsBounds, int len, int dataPos, params int[] strsPos) {
			var res = strsPos[0] != 0? BitConverter.ToString(bytes, dataPos, strsPos[0]) + '-': "";
			var strs = new string[strsPos.Length];
			for(int i = 0; i < strsPos.Length; i++) {
				if(i + 1 == strsPos.Length) {
					if(strsPos[i] + 2 < len) {
						var start = strsPos[i] + 2;
						res += "S-" + BitConverter.ToString(bytes, dataPos + start, len - start);
					} else {
						res += "S";
					}
				} else {
					var start = strsPos[i] + 2;
					res += "S-" + BitConverter.ToString(bytes, dataPos + start, strsPos[i + 1] - start) + '-';
				}
				var strStart = BitConverter.ToUInt16(bytes, dataPos + strsPos[i]);
				int strEnd = strStart;
				while(bytes[strEnd] != 0) {
					strEnd++;
				}
				if(strStart < strsBounds.Item1) {
					strsBounds.Item1 = strStart;
				}
				if(strEnd + 1 > strsBounds.Item2) {
					strsBounds.Item2 = strEnd + 1;
				}
				strs[i] = UseBig5? Big5.Decode(bytes.AsSpan(strStart..strEnd)):
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
			var retried = false;
			retry:
			var res = "header." + bytes[0] + ' ' + BitConverter.ToString(bytes, 1, bytes[0] - 1);
			var pos = (int) bytes[0];
			var strsBounds = (0xFFFFFF, 0);
			try {
				for(int i = 0; i < SECTIONS.Length; i++) {
					var section = SECTIONS[i];
					var endOfSection = pos + section.SizeJp;
					//if(encoding.BodyName == "big5") {
					//	endOfSection += section.SizeDiffCn;
					//}
					while(pos < endOfSection) {
						var instDump = '\n' + section.Name + '.' + section.ItemLen + ' ';
						if(section.Name == "name") {
							instDump += DumpCmdWithStrings(bytes, ref strsBounds, section.ItemLen, pos, 0, 4);
						} else if(section.Name == "route") {
							instDump += DumpCmdWithStrings(bytes, ref strsBounds, section.ItemLen, pos, 4, 8);
						} else if(section.Name == "route2") {
							instDump += DumpCmdWithStrings(bytes, ref strsBounds, section.ItemLen, pos, 8, 12);
						} else if(section.Name == "scene" || section.Name == "string" || section.Name == "title") {
							instDump += DumpCmdWithStrings(bytes, ref strsBounds, section.ItemLen, pos, 0);
						} else {
							instDump += BitConverter.ToString(bytes, pos, section.ItemLen);
						}
						res += instDump;
						pos += section.ItemLen;
					}
				}
			} catch(DecoderFallbackException) {
				if(retried) {
					throw new Exception("could not decode DATA.BIN");
				}
				retried = true;
				UseBig5 = !UseBig5;
				goto retry;
			}
			res += "\ntrailer." + (bytes.Length - strsBounds.Item2) + ' ' + BitConverter.ToString(bytes, strsBounds.Item2);
			Dumps[filename] = res;
			return res;
		}
	}

	public enum Opcode {
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