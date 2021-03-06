﻿// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2016  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

namespace TS3Client
{
	using Messages;
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Net.Sockets;
	using System.Text;
	using System.Threading;

	using ClientUidT = System.String;
	using ClientDbIdT = System.UInt64;
	using ClientIdT = System.UInt16;
	using ChannelIdT = System.UInt64;
	using ServerGroupIdT = System.UInt64;
	using ChannelGroupIdT = System.UInt64;

	public class FileTransferManager
	{
		internal Ts3BaseFunctions parent;
		private Queue<FileTransferToken> transferQueue;
		private static Random Random = new Random();
		private Thread workerThread;
		private bool threadEnd = false;
		private ushort transferIdCnt;

		public FileTransferManager(Ts3BaseFunctions ts3connection)
		{
			parent = ts3connection;
			Util.Init(ref transferQueue);
		}

		public FileTransferToken UploadFile(System.IO.FileInfo file, ChannelIdT channel, string path, bool overwrite = false, string channelPassword = "")
			=> UploadFile(file.Open(FileMode.Open, FileAccess.Read), channel, path, overwrite, channelPassword);

		public FileTransferToken UploadFile(Stream stream, ChannelIdT channel, string path, bool overwrite = false, string channelPassword = "")
		{
			ushort cftid = GetFreeTransferId();
			var request = parent.FileTransferInitUpload(channel, path, channelPassword, cftid, stream.Length, overwrite, false);
			if (!string.IsNullOrEmpty(request.Message))
				throw new Ts3Exception(request.Message);
			var token = new FileTransferToken(stream, request, channel, path, channelPassword, stream.Length);
			StartWorker(token);
			return token;
		}

		public FileTransferToken DownloadFile(System.IO.FileInfo file, ChannelIdT channel, string path, string channelPassword = "")
			=> DownloadFile(file.Open(FileMode.Create, FileAccess.Write), channel, path, true, channelPassword);

		public FileTransferToken DownloadFile(Stream stream, ChannelIdT channel, string path, bool closeStream, string channelPassword = "")
		{
			ushort cftid = GetFreeTransferId();
			var request = parent.FileTransferInitDownload(channel, path, channelPassword, cftid, 0);
			if (!string.IsNullOrEmpty(request.Message))
				throw new Ts3Exception(request.Message);
			var token = new FileTransferToken(stream, request, channel, path, channelPassword, 0) { CloseStreamWhenDone = closeStream };
			StartWorker(token);
			return token;
		}

		private void StartWorker(FileTransferToken token)
		{
			lock (transferQueue)
			{
				transferQueue.Enqueue(token);

				if (threadEnd || workerThread == null || !workerThread.IsAlive)
				{
					workerThread = new Thread(TransferLoop);
					workerThread.Start();
				}
			}
		}

		private ushort GetFreeTransferId()
		{
			return ++transferIdCnt;
		}

		public void Resume(FileTransferToken token)
		{
			lock (token)
			{
				if (token.Status != TransferStatus.Cancelled)
					throw new Ts3Exception("Only cancelled transfers can be resumed");

				if (token.Direction == TransferDirection.Upload)
				{
					var request = parent.FileTransferInitUpload(token.ChannelId, token.Path, token.ChannelPassword, token.ClientTransferId, token.Size, false, true);
					if (!string.IsNullOrEmpty(request.Message))
						throw new Ts3Exception(request.Message);
					token.ServerTransferId = request.ServerFileTransferId;
					token.SeekPosition = request.SeekPosistion;
					token.Port = request.Port;
					token.TransferKey = request.FileTransferKey;
				}
				else // Download
				{
					var request = parent.FileTransferInitDownload(token.ChannelId, token.Path, token.ChannelPassword, token.ClientTransferId, token.LocalStream.Position);
					if (!string.IsNullOrEmpty(request.Message))
						throw new Ts3Exception(request.Message);
					token.ServerTransferId = request.ServerFileTransferId;
					token.SeekPosition = -1;
					token.Port = request.Port;
					token.TransferKey = request.FileTransferKey;
				}
				token.Status = TransferStatus.Waiting;
			}
			StartWorker(token);
		}

		public void Abort(FileTransferToken token, bool delete = false)
		{
			lock (token)
			{
				if (token.Status != TransferStatus.Trasfering && token.Status != TransferStatus.Waiting)
					return;
				parent.FileTransferStop(token.ServerTransferId, delete);
				token.Status = TransferStatus.Cancelled;
				if (delete && token.CloseStreamWhenDone)
				{
					token.LocalStream.Close();
				}
			}
		}

		public void Wait(FileTransferToken token)
		{
			while (token.Status == TransferStatus.Waiting || token.Status == TransferStatus.Trasfering)
				Thread.Sleep(10);
		}

		public FileTransfer GetStats(FileTransferToken token)
		{
			lock (token)
			{
				if (token.Status != TransferStatus.Trasfering)
					return null;
			}
			try { return parent.FileTransferList().FirstOrDefault(x => x.ServerFileTransferId == token.ServerTransferId); }
			// catch case when transfer is not found (probably already over or not yet started)
			catch (Ts3CommandException ts3ex) when (ts3ex.ErrorStatus.Id == Ts3ErrorCode.database_empty_result) { return null; }
		}

		private void TransferLoop()
		{
			while (true)
			{
				FileTransferToken token;
				lock (transferQueue)
				{
					if (transferQueue.Count <= 0)
					{
						threadEnd = true;
						break;
					}
					token = transferQueue.Dequeue();
				}

				try
				{
					lock (token)
					{
						if (token.Status != TransferStatus.Waiting)
							continue;
						token.Status = TransferStatus.Trasfering;
					}

					using (var client = new TcpClient())
					{
						client.Connect(parent.ConnectionData.Hostname, token.Port);
						using (var stream = client.GetStream())
						{
							byte[] keyBytes = Encoding.ASCII.GetBytes(token.TransferKey);
							stream.Write(keyBytes, 0, keyBytes.Length);

							if (token.SeekPosition >= 0 && token.LocalStream.Position != token.SeekPosition)
								token.LocalStream.Seek(token.SeekPosition, SeekOrigin.Begin);

							if (token.Direction == TransferDirection.Upload)
							{
								token.LocalStream.CopyTo(stream);
							}
							else // Download
							{
								// try to preallocate space
								try { token.LocalStream.SetLength(token.Size); }
								catch (NotSupportedException) { }

								stream.CopyTo(token.LocalStream);
							}
							lock (token)
							{
								if (token.Status == TransferStatus.Trasfering && token.LocalStream.Position == token.Size)
								{
									token.Status = TransferStatus.Done;
									if (token.CloseStreamWhenDone)
										token.LocalStream.Close();
								}
							}
						}
					}
				}
				catch (IOException) { }
				finally
				{
					lock (token)
					{
						if (token.Status != TransferStatus.Done && token.Status != TransferStatus.Cancelled)
							token.Status = TransferStatus.Failed;
					}
				}
			}
			threadEnd = true;
		}
	}

	public class FileTransferToken
	{
		public Stream LocalStream { get; }
		public TransferDirection Direction { get; }
		public ChannelIdT ChannelId { get; }
		public string Path { get; }
		public long Size { get; }
		public ushort ClientTransferId { get; }
		public ushort ServerTransferId { get; internal set; }
		public string ChannelPassword { get; set; }
		public ushort Port { get; internal set; }
		public long SeekPosition { get; internal set; }
		public string TransferKey { get; internal set; }
		public bool CloseStreamWhenDone { get; set; }

		public TransferStatus Status { get; internal set; }

		public FileTransferToken(Stream localStream, FileUpload upload, ChannelIdT channelId,
			string path, string channelPassword, long size)
			: this(localStream, upload.ClientFileTransferId, upload.ServerFileTransferId, TransferDirection.Upload,
				channelId, path, channelPassword, upload.Port, upload.SeekPosistion, upload.FileTransferKey, size)
		{ }

		public FileTransferToken(Stream localStream, FileDownload download, ChannelIdT channelId,
			string path, string channelPassword, long seekPos)
			: this(localStream, download.ClientFileTransferId, download.ServerFileTransferId, TransferDirection.Download,
				channelId, path, channelPassword, download.Port, seekPos, download.FileTransferKey, download.Size)
		{ }

		public FileTransferToken(Stream localStream, ushort cftid, ushort sftid,
			TransferDirection dir, ChannelIdT channelId, string path, string channelPassword, ushort port, long seekPos,
			string transferKey, long size)
		{
			CloseStreamWhenDone = false;
			Status = TransferStatus.Waiting;
			LocalStream = localStream;
			Direction = dir;
			ClientTransferId = cftid;
			ServerTransferId = sftid;
			ChannelId = channelId;
			Path = path;
			ChannelPassword = channelPassword;
			Port = port;
			SeekPosition = seekPos;
			TransferKey = transferKey;
			Size = size;
		}
	}

	public enum TransferDirection
	{
		Upload,
		Download,
	}

	public enum TransferStatus
	{
		Waiting,
		Trasfering,
		Done,
		Cancelled,
		Failed,
	}
}
