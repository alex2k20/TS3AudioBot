using System;
using System.Runtime.Serialization;

namespace TS3AudioBot
{
	public static class Error
	{
		public static AudioBotException LocalStr(string text) => new AudioBotException().LocalStr(text);
		public static AudioBotException Exception(Exception ex) => new AudioBotException().Exception(ex);
		public static AudioBotException Str(string text) => new AudioBotException().Str(text);

		public static AudioBotException LocalStr(this AudioBotException ex, string text) { ex.LocalStr = text; return ex; }
		public static AudioBotException Exception(this AudioBotException ex, Exception baseEx) { ex.InnerCustomException = baseEx; return ex; }
		public static AudioBotException Str(this AudioBotException ex, string text) { ex.Str = text; return ex; }
		public static void Throw(this AudioBotException ex) => throw ex;
	}

	[Serializable]
	public class AudioBotException : Exception
	{
		public string? LocalStr { get; set; }
		public string? Str { get; set; }
		public Exception? InnerCustomException { get; set; }

		public override string Message => LocalStr ?? Str ?? "";

		public AudioBotException()
			: base()
		{ }

		public AudioBotException(string message)
			: base(null)
		{
			LocalStr = message;
		}

		public AudioBotException(string message, Exception inner)
			: base(null, inner)
		{
			LocalStr = message;
		}

		protected AudioBotException(SerializationInfo info, StreamingContext context)
			: base(info, context)
		{ }
	}
}