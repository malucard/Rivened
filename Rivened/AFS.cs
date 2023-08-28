using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using GLib;

namespace Rivened {
	public class AFS {
		public class Entry {
			public string Name;
			public ushort Year = 0;
			public ushort Month = 0;
			public ushort Day = 0;
			public ushort Hour = 0;
			public ushort Minute = 0;
			public ushort Second = 0;
			public int Size {get; private set;}
			private readonly uint Position;
			public byte[] Data {get; private set;}

			public Entry(string name, int size, uint position) {
				Name = name;
				Size = size;
				Position = position;
				Data = null;
			}

			public Entry(string name, byte[] data) {
				Name = name;
				Size = data.Length;
				Position = 0;
				Data = data;
			}

			public byte[] Load(AFS afs) {
				if(Data != null) {
					return Data;
				}
				Trace.Assert(Position != 0);
				using var handle = File.OpenRead(afs.LoadPath.Path);
				Data = new byte[Size];
				handle.Position = Position;
				handle.Read(Data, 0, Size);
				handle.Dispose();
				return Data;
			}

			public void SetData(byte[] data) {
				Data = data;
				Size = data.Length;
			}
		}

		public readonly IFile LoadPath;
		public readonly IFile SavePath;
		public Entry[] Entries;

		public AFS(IFile path): this(path, path) {}

		protected AFS(IFile savePath, IFile loadPath) {
			LoadPath = loadPath;
			SavePath = savePath;
			using var handle = File.OpenRead(loadPath.Path);
			using var afs = new BinaryReader(handle);
			var sig = afs.ReadUInt32();
			if(sig != ((uint) 'A' | (uint) 'F' << 8 | (uint) 'S' << 16)) {
				throw new Exception(loadPath + " is not an AFS file, sig is " + string.Format("{x}", sig));
			}
			var count = afs.ReadUInt32();
			Trace.Assert(count <= 0xfffff);
			Entries = new Entry[count];
			uint end = 0;
			for(uint i = 0; i < count; i++) {
				uint offset = afs.ReadUInt32();
				uint size = afs.ReadUInt32();
				if(offset + size > end) { // nonsense because they're linear, but... y'know, just in case...
					end = offset + size;
				}
				Entries[i] = new Entry(null, (int) size, offset);
			}
			end = Align(end, 0x800);
			afs.BaseStream.Position = end;
			for(uint i = 0; i < count; i++) {
				Entries[i].Name = Encoding.ASCII.GetString(afs.ReadBytes(32)).TrimEnd('\0');
				Entries[i].Year = afs.ReadUInt16();
				Entries[i].Month = afs.ReadUInt16();
				Entries[i].Day = afs.ReadUInt16();
				Entries[i].Hour = afs.ReadUInt16();
				Entries[i].Minute = afs.ReadUInt16();
				Entries[i].Second = afs.ReadUInt16();
				afs.ReadUInt32();
			}
			handle.Dispose();
		}

		public void Save() {
			using var stream = new MemoryStream();
			using var wr = new BinaryWriter(stream);
			wr.Write((byte) 'A');
			wr.Write((byte) 'F');
			wr.Write((byte) 'S');
			wr.Write((byte) 0);
			wr.Write((uint) Entries.Length);
			foreach(var entry in Entries) {
				wr.Write((uint) 0);
				wr.Write((uint) entry.Load(this).Length);
			}
			var headerEnd = Align((uint) stream.Position, 0x800);
			var pos = headerEnd;
			for(var i = 0; i < Entries.Length; i++) {
				wr.Flush();
				stream.Position = i * 8 + 8;
				wr.Write((uint) pos);
				wr.Flush();
				stream.Position = pos;
				wr.Write(Entries[i].Load(this));
				pos = Align(pos + (uint) Entries[i].Size, 0x800);
			}
			wr.Flush();
			stream.Position = headerEnd - 8;
			wr.Write((uint) pos);
			wr.Write((uint) (0x30 * Entries.Length));
			wr.Flush();
			stream.Position = pos;
			foreach(var entry in Entries) {
				for(int i = 0; i < 0x20; i++) {
					wr.Write((byte) (i < entry.Name.Length? entry.Name[i]: 0));
				}
				wr.Write((ushort) entry.Year);
				wr.Write((ushort) entry.Month);
				wr.Write((ushort) entry.Day);
				wr.Write((ushort) entry.Hour);
				wr.Write((ushort) entry.Minute);
				wr.Write((ushort) entry.Second);
				wr.Write((uint) entry.Size);
			}
			using var file = File.OpenWrite(SavePath.Path);
			stream.Flush();
			file.Write(stream.ToArray());
			Program.Log("Saved to " + SavePath.Path);
		}

		public static uint Align(uint offset, uint alignment) {
			return (offset + alignment - 1) & ~(alignment - 1);
		}

		public AFS(IFile path, Entry[] entries) {
			LoadPath = path;
			SavePath = path;
			Entries = entries;
		}

		public AFS Clone(IFile otherPath) {
			return new AFS(otherPath, (Entry[]) Entries.Clone());
		}
	}
}
