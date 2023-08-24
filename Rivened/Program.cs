using System;
using System.IO;
using System.Net.Http;
using System.Text;
using Gtk;

namespace Rivened {
	class Program {
		public static string LatestLog = "";

		public static void Log(string msg) {
			LatestLog = '[' + DateTime.Now.ToLocalTime().ToString("H:mm:ss") + "] " + msg;
			Console.WriteLine(msg);
			MainWindow.Instance.UpdateLog();
		}

		public static Encoding SJIS {get; private set;}

		[STAThread]
		public static void Main(string[] args) {
			Big5.Load();
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			SJIS = (Encoding) Encoding.GetEncoding("Shift-JIS").Clone();
			SJIS.DecoderFallback = DecoderFallback.ExceptionFallback;
			SJIS.EncoderFallback = EncoderFallback.ExceptionFallback;
			Application.Init();

			var app = new Application("org.Rivened.Rivened", GLib.ApplicationFlags.None);
			app.Register(GLib.Cancellable.Current);

			var win = new MainWindow();
			app.AddWindow(win);

			win.Show();
			Application.Run();
		}
	}
}
