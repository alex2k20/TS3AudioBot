// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using CliWrap;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TSLib.Audio;
using TSLib.Helper;
using TSLib.Scheduler;

namespace TS3AudioBot.Audio
{
	public class FfmpegProducer : IPlayerSource, IDisposable
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private readonly Id id;
		private static readonly Regex FindDurationMatch = new Regex(@"^\s*Duration: (\d+):(\d\d):(\d\d).(\d\d)", Util.DefaultRegexConfig);
		private static readonly Regex IcyMetadataMacher = new Regex("((\\w+)='(.*?)';\\s*)+", Util.DefaultRegexConfig);
		private const string PreLinkConf = "-hide_banner -nostats -threads 1 -i \"";
		private const string PostLinkConf = "\" -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		private const string LinkConfIcy = "-hide_banner -nostats -threads 1 -i pipe:0 -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		private static readonly TimeSpan retryOnDropBeforeEnd = TimeSpan.FromSeconds(10);

		private readonly ConfToolsFfmpeg config;

		public event EventHandler? OnSongEnd;
		public event EventHandler<SongInfoChanged>? OnSongUpdated;

		private readonly DedicatedTaskScheduler scheduler;
		private FfmpegInstance? ffmpegInstance;
		private PlayTask? currentPlayTask;
		public SampleInfo SampleInfo { get; } = SampleInfo.OpusMusic;
		public IAudioPassiveConsumer? OutStream { get; set; }

		public FfmpegProducer(ConfToolsFfmpeg config, DedicatedTaskScheduler scheduler, Id id)
		{
			this.config = config;
			this.scheduler = scheduler;
			this.id = id;
		}

		public Task AudioStart(string url, TimeSpan? startOff = null)
		{
			StartFfmpegProcess(url, startOff ?? TimeSpan.Zero);
			return Task.CompletedTask;
		}

		public Task AudioStartIcy(string url)
		{
			PlayIcyTask(url);
			return Task.CompletedTask;
		}

		public void AudioStop()
		{
			StopFfmpegProcess();
		}

		public TimeSpan? Length => GetCurrentSongLength();

		public TimeSpan? Position => ffmpegInstance?.AudioTimer.SongPosition;

		public Task Seek(TimeSpan position) { SetPosition(position); return Task.CompletedTask; }

		public async ValueTask<(int read, Meta? meta)> ReadAsync(Memory<byte> data)
		{
			int read;

			var instance = ffmpegInstance;

			if (instance is null)
				return (0, default);

			try
			{
				read = await instance.FfmpegProcess.StandardOutput.BaseStream.ReadAsync(data);
			}
			catch (Exception ex)
			{
				read = 0;
				Log.Debug(ex, "Can't read ffmpeg");
			}

			if (read == 0)
			{
				AssertNotMainScheduler();

				var (ret, triggerEndSafe) = instance.IsIcyStream
					? OnReadEmptyIcy(instance)
					: OnReadEmpty(instance);
				if (ret)
					return (0, default);

				if (instance.FfmpegProcess.HasExitedSafe())
				{
					Log.Trace("Ffmpeg has exited");
					AudioStop();
					triggerEndSafe = true;
				}

				if (triggerEndSafe)
				{
					OnSongEnd?.Invoke(this, EventArgs.Empty);
					return (0, default);
				}
			}

			instance.HasTriedToReconnect = false;
			instance.AudioTimer.PushBytes(read);
			return (read, default);
		}

		private (bool ret, bool trigger) OnReadEmpty(FfmpegInstance instance)
		{
			if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
			{
				var expectedStopLength = GetCurrentSongLength();
				Log.Trace("Expected song length {0}", expectedStopLength);
				if (expectedStopLength != TimeSpan.Zero)
				{
					var actualStopPosition = instance.AudioTimer.SongPosition;
					Log.Trace("Actual song position {0}", actualStopPosition);
					if (actualStopPosition + retryOnDropBeforeEnd < expectedStopLength)
					{
						Log.Debug("Connection to song lost, retrying at {0}", actualStopPosition);
						instance.HasTriedToReconnect = true;
						if (SetPosition(actualStopPosition).Get(out var newInstance, out var error))
						{
							newInstance.HasTriedToReconnect = true;
							return (true, false);
						}
						else
						{
							Log.Debug("Retry failed {0}", error);
							return (false, true);
						}
					}
				}
			}
			return (false, false);
		}

		private (bool ret, bool trigger) OnReadEmptyIcy(FfmpegInstance instance)
		{
			AssertNotMainScheduler();

			if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
			{
				Log.Debug("Connection to stream lost, retrying...");
				instance.HasTriedToReconnect = true;
				var newInstance = StartFfmpegProcessIcy(instance.ReconnectUrl).Result;
				if (newInstance.Ok)
				{
					newInstance.Value.HasTriedToReconnect = true;
					return (true, false);
				}
				else
				{
					Log.Debug("Retry failed {0}", newInstance.Error);
					return (false, true);
				}
			}
			return (false, false);
		}

		private R<FfmpegInstance, string> SetPosition(TimeSpan value)
		{
			if (value < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(value));
			var instance = ffmpegInstance;
			if (instance is null)
				return "No instance running";
			if (instance.IsIcyStream)
				return "Cannot seek icy stream";
			var lastLink = instance.ReconnectUrl;
			if (lastLink is null)
				return "No current url active";
			return StartFfmpegProcess(lastLink, value);
		}

		private R<FfmpegInstance, string> StartFfmpegProcess(string url, TimeSpan? offsetOpt)
		{
			StopFfmpegProcess();
			Log.Trace("Start request {0}", url);

			string arguments;
			var offset = offsetOpt ?? TimeSpan.Zero;
			if (offset > TimeSpan.Zero)
			{
				var seek = string.Format(CultureInfo.InvariantCulture, @"-ss {0:hh\:mm\:ss\.fff}", offset);
				arguments = string.Concat(seek, " ", PreLinkConf, url, PostLinkConf, " ", seek);
			}
			else
			{
				arguments = string.Concat(PreLinkConf, url, PostLinkConf);
			}

			var newInstance = new FfmpegInstance(
				url,
				new PreciseAudioTimer(SampleInfo)
				{
					SongPositionOffset = offset,
				});

			return StartFfmpegProcessInternal(newInstance, arguments);
		}

		private async Task<R<FfmpegInstance, string>> StartFfmpegProcessIcy(string url)
		{
			StopFfmpegProcess();
			Log.Trace("Start icy-stream request {0}", url);

			try
			{
				var response = await WebWrapper
					.Request(url)
					.WithHeader("Icy-MetaData", "1")
					.UnsafeResponse();

				if (!int.TryParse(response.Headers.GetSingle("icy-metaint"), out var metaint))
				{
					response.Dispose();
					return "Invalid icy stream tags";
				}

				var stream = await response.Content.ReadAsStreamAsync();
				var newInstance = new FfmpegInstance(
					url,
					new PreciseAudioTimer(SampleInfo),
					stream,
					metaint)
				{
					OnMetaUpdated = e => OnSongUpdated?.Invoke(this, e)
				};

				new Thread(() => newInstance.ReadStreamLoop(id))
				{
					Name = $"IcyStreamReader[{id}]",
				}.Start();

				return StartFfmpegProcessInternal(newInstance, LinkConfIcy);
			}
			catch (Exception ex)
			{
				var error = $"Unable to create icy-stream ({ex.Message})";
				Log.Warn(ex, error);
				return error;
			}
		}

		private void PlayIcyTask(string url)
		{
			scheduler.VerifyOwnThread();

			if (currentPlayTask != null)
			{
				currentPlayTask.Cts.Cancel();
				currentPlayTask = null;
			}

			var cts = new CancellationTokenSource();
			var task = PlayIcyTask(url, cts);
			currentPlayTask = new PlayTask(task, cts);
		}

		private async Task PlayIcyTask(string url, CancellationTokenSource cts)
		{
			try
			{
				using var _ = cts;
				using var response = await WebWrapper
					.Request(url)
					.WithHeader("Icy-MetaData", "1")
					.UnsafeResponse(cts.Token);

				if (!int.TryParse(response.Headers.GetSingle("icy-metaint"), out var metaint))
				{
					Log.Debug("Invalid icy stream tags");
					return;
				}

				using var stream = await response.Content.ReadAsStreamAsync();
				using var icyStream = new IcyStream(stream, metaint)
				{
					OnMetaUpdated = e => OnSongUpdated?.Invoke(this, e)
				};

				await Cli.Wrap(config.Path.Value)
					.WithArguments(LinkConfIcy)
					.WithStandardInputPipe(PipeSource.FromStream(icyStream))
					.WithStandardOutputPipe(PipeTarget.Null)
					.WithStandardErrorPipe(PipeTarget.Null)
					.WithValidation(CommandResultValidation.None)
					.ExecuteAsync(cts.Token);
			}
			catch (Exception ex)
			{
				Log.Warn(ex, $"Icy-stream cancelled ({ex.Message})");
			}
		}

		private R<FfmpegInstance, string> StartFfmpegProcessInternal(FfmpegInstance instance, string arguments)
		{
			try
			{
				instance.FfmpegProcess.StartInfo = new ProcessStartInfo
				{
					FileName = config.Path.Value,
					Arguments = arguments,
					RedirectStandardOutput = true,
					RedirectStandardInput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};
				instance.FfmpegProcess.EnableRaisingEvents = true;

				Log.Debug("Starting ffmpeg with {0}", arguments);
				instance.FfmpegProcess.ErrorDataReceived += instance.FfmpegProcess_ErrorDataReceived;
				instance.FfmpegProcess.Start();
				instance.FfmpegProcess.BeginErrorReadLine();

				instance.AudioTimer.Start();

				var oldInstance = Interlocked.Exchange(ref ffmpegInstance, instance);
				oldInstance?.Close();

				return instance;
			}
			catch (Exception ex)
			{
				var error = ex is Win32Exception
					? $"Ffmpeg could not be found ({ex.Message})"
					: $"Unable to create stream ({ex.Message})";
				Log.Error(ex, error);
				instance.Close();
				StopFfmpegProcess();
				return error;
			}
		}

		private void StopFfmpegProcess()
		{
			var oldInstance = Interlocked.Exchange(ref ffmpegInstance, null);
			if (oldInstance != null)
			{
				oldInstance.OnMetaUpdated = null;
				oldInstance.Close();
			}
		}

		private TimeSpan? GetCurrentSongLength() => ffmpegInstance?.ParsedSongLength;

		private void AssertNotMainScheduler()
		{
			if (TaskScheduler.Current == scheduler)
				throw new Exception("Cannot read on own scheduler. Throwing to prevent deadlock");
		}

		public void Dispose()
		{
			StopFfmpegProcess();
		}

		private class FfmpegInstance
		{
			public Process FfmpegProcess { get; }
			public bool HasTriedToReconnect { get; set; }
			public string ReconnectUrl { get; }
			public bool IsIcyStream => IcyStream != null;

			public PreciseAudioTimer AudioTimer { get; }
			public TimeSpan? ParsedSongLength { get; set; } = null;

			public Stream? IcyStream { get; }
			public int IcyMetaInt { get; }
			public bool Closed { get; set; }

			public Action<SongInfoChanged>? OnMetaUpdated;

			public FfmpegInstance(string url, PreciseAudioTimer timer) : this(url, timer, null!, 0) { }
			public FfmpegInstance(string url, PreciseAudioTimer timer, Stream icyStream, int icyMetaInt)
			{
				FfmpegProcess = new Process();
				ReconnectUrl = url;
				AudioTimer = timer;
				IcyStream = icyStream;
				IcyMetaInt = icyMetaInt;

				HasTriedToReconnect = false;
			}

			public void Close()
			{
				Closed = true;

				try
				{
					if (!FfmpegProcess.HasExitedSafe())
						FfmpegProcess.Kill();
				}
				catch { }
				try { FfmpegProcess.CancelErrorRead(); } catch { }
				try { FfmpegProcess.StandardInput.Dispose(); } catch { }
				try { FfmpegProcess.StandardOutput.Dispose(); } catch { }
				try { FfmpegProcess.Dispose(); } catch { }

				IcyStream?.Dispose();
			}

			public void FfmpegProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
			{
				if (e.Data is null)
					return;

				if (sender != FfmpegProcess)
					throw new InvalidOperationException("Wrong process associated to event");

				if (ParsedSongLength is null)
				{
					var match = FindDurationMatch.Match(e.Data);
					if (!match.Success)
						return;

					int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
					int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
					int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
					int millisec = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) * 10;
					ParsedSongLength = new TimeSpan(0, hours, minutes, seconds, millisec);
				}

				//if (!HasIcyTag && e.Data.AsSpan().TrimStart().StartsWith("icy-".AsSpan()))
				//{
				//	HasIcyTag = true;
				//}
			}

			public void ReadStreamLoop(Id id)
			{
				if (IcyStream is null)
					throw new InvalidOperationException("Instance is not an icy stream");

				Tools.SetLogId(id.ToString());
				const int IcyMaxMeta = 255 * 16;
				const int ReadBufferSize = 4096;

				int errorCount = 0;
				var buffer = new byte[Math.Max(ReadBufferSize, IcyMaxMeta)];
				int readCount = 0;

				while (!Closed)
				{
					try
					{
						while (readCount < IcyMetaInt)
						{
							int read = IcyStream.Read(buffer, 0, Math.Min(ReadBufferSize, IcyMetaInt - readCount));
							if (read == 0)
							{
								Close();
								return;
							}
							readCount += read;
							FfmpegProcess.StandardInput.BaseStream.Write(buffer, 0, read);
							errorCount = 0;
						}
						readCount = 0;

						var metaByte = IcyStream.ReadByte();
						if (metaByte < 0)
						{
							Close();
							return;
						}

						if (metaByte > 0)
						{
							metaByte *= 16;
							while (readCount < metaByte)
							{
								int read = IcyStream.Read(buffer, 0, metaByte - readCount);
								if (read == 0)
								{
									Close();
									return;
								}
								readCount += read;
							}
							readCount = 0;

							var metaString = Tools.Utf8Encoder.GetString(buffer, 0, metaByte).TrimEnd('\0');
							Log.Debug("Meta: {0}", metaString);
							OnMetaUpdated?.Invoke(ParseIcyMeta(metaString));
						}
					}
					catch (Exception ex)
					{
						errorCount++;
						if (errorCount >= 50)
						{
							Log.Error(ex, "Failed too many times trying to access ffmpeg. Closing stream.");
							Close();
							return;
						}

						if (ex is InvalidOperationException)
						{
							Log.Debug(ex, "Waiting for ffmpeg");
							Thread.Sleep(100);
						}
						else
						{
							Log.Debug(ex, "Stream read/write error");
						}
					}
				}
			}

			private static SongInfoChanged ParseIcyMeta(string metaString)
			{
				var songInfo = new SongInfoChanged();
				var match = IcyMetadataMacher.Match(metaString);
				if (match.Success)
				{
					for (int i = 0; i < match.Groups[1].Captures.Count; i++)
					{
						switch (match.Groups[2].Captures[i].Value.ToUpperInvariant())
						{
						case "STREAMTITLE":
							songInfo.Title = match.Groups[3].Captures[i].Value;
							break;
						}
					}
				}
				return songInfo;
			}
		}
	}

	public class IcyStream : Stream
	{
		private static readonly Regex IcyMetadataMacher = new Regex("((\\w+)='(.*?)';\\s*)+", Util.DefaultRegexConfig);

		public int IcyMetaInt { get; }
		public Stream BaseStream { get; }
		public Action<SongInfoChanged>? OnMetaUpdated { get; set; }
		private byte[]? icyMetaBuffer = null;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => 0;
		public override long Position { get => 0; set => throw new NotSupportedException(); }

		private int readCount;

		public IcyStream(Stream baseStream, int icyMetaInt)
		{
			BaseStream = baseStream;
			IcyMetaInt = icyMetaInt;

			readCount = 0;
		}

		public override void Flush() => throw new NotSupportedException();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			const int IcyMaxMeta = 255 * 16;
			const int ReadBufferSize = 4096;

			try
			{
				while (true)
				{
					if (readCount < IcyMetaInt)
					{
						int read = await BaseStream.ReadAsync(buffer, 0, Math.Min(ReadBufferSize, IcyMetaInt - readCount), cancellationToken);
						if (read == 0)
						{
							// Close(); ?
							return 0;
						}
						readCount += read;
						return read;
					}
					readCount = 0;

					byte[] metaBuffer;
					if (buffer.Length >= IcyMaxMeta)
					{
						metaBuffer = buffer;
					}
					else
					{
						icyMetaBuffer ??= new byte[IcyMaxMeta];
						metaBuffer = icyMetaBuffer;
					}

					var metaByteRead = await BaseStream.ReadAsync(metaBuffer, 0, 1, cancellationToken);
					if (metaByteRead == 0)
					{
						// Close();
						return 0;
					}
					var metaByte = metaBuffer[0];

					if (metaByte > 0)
					{
						metaByte *= 16;
						while (readCount < metaByte)
						{
							int read = await BaseStream.ReadAsync(metaBuffer, 0, metaByte - readCount, cancellationToken);
							if (read == 0)
							{
								// Close();
								return 0;
							}
							readCount += read;
						}
						readCount = 0;

						var metaString = Tools.Utf8Encoder.GetString(metaBuffer, 0, metaByte).TrimEnd('\0');
						//Log.Debug("Meta: {0}", metaString);
						OnMetaUpdated?.Invoke(ParseIcyMeta(metaString));
					}
				}
			}
			catch (Exception ex)
			{
				return 0;
			}
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		private static SongInfoChanged ParseIcyMeta(string metaString)
		{
			var songInfo = new SongInfoChanged();
			var match = IcyMetadataMacher.Match(metaString);
			if (match.Success)
			{
				for (int i = 0; i < match.Groups[1].Captures.Count; i++)
				{
					switch (match.Groups[2].Captures[i].Value.ToUpperInvariant())
					{
					case "STREAMTITLE":
						songInfo.Title = match.Groups[3].Captures[i].Value;
						break;
					}
				}
			}
			return songInfo;
		}

		protected override void Dispose(bool disposing)
		{
			icyMetaBuffer = null;
			OnMetaUpdated = null;
			BaseStream.Dispose();
		}
	}

	public class PlayTask
	{
		public Task RunningTask { get; }
		public CancellationTokenSource Cts { get; }

		public PlayTask(Task runningTask, CancellationTokenSource cancellationToken)
		{
			RunningTask = runningTask;
			Cts = cancellationToken;
		}
	}

	// Icy: IcyLoop +=> FFmpeg +=> Buffer -=> TimePipe
	// Nrm:             FFmpeg +=> Buffer -=> TimePipe
}
