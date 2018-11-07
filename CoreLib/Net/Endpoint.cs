﻿using Dullahan.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Dullahan.Net
{
	/// <summary>
	/// Manages the connection to a Dullahan server/client
	/// </summary>
	public class Endpoint : ILogWriter, ILogReader
	{
		#region STATIC_VARS

		public const int DEFAULT_PORT = 8080;

		private const string DEBUG_TAG = "[DULCON]";

		//length of the data buffer
		private const int DB_LENGTH = 1024;
		#endregion

		#region INSTANCE_VARS

		public string Name { get; set; }

		private IPAddress address;
		private int port;

		private TcpClient client;
		private NetworkStream netStream;
		private SslStream secureStream;

		/// <summary>
		/// Connected to the remote host.
		/// </summary>
		public bool Connected { get { return client != null && client.Connected; } }

		private object stateMutex = new object ();

		private int readingCount;
		/// <summary>
		/// Receiving data from the remote host.
		/// </summary>
		public bool Reading
		{
			get { return readingCount > 0; }
			private set
			{
				lock (stateMutex)
				{
					readingCount += (value ? 1 : -1);
					if (readingCount < 0)
						readingCount = 0;
				}
			}
		}

		private int sendingCount;
		/// <summary>
		/// Sending data to the remote host.
		/// </summary>
		public bool Sending
		{
			get { return sendingCount > 0; }
			private set
			{
				lock (stateMutex)
				{
					sendingCount += (value ? 1 : -1);
					if (sendingCount < 0)
						sendingCount = 0;
				}
			}
		}

		/// <summary>
		/// Was connected and lost connection, or attempted connection failed.
		/// </summary>
		public bool Disconnected { get; private set; }

		/// <summary>
		/// The availability state of the Client. 
		/// Returns true if connected and no operations are underway.
		/// </summary>
		public bool Idle
		{
			get
			{
				return !Disconnected && Connected && !Reading && !Sending;
			}
		}

		/// <summary>
		/// Determines what "direction" data will flow to/from.
		/// </summary>
		public FlowState Flow { get; set; } = FlowState.bidirectional;

		/// <summary>
		/// Data received
		/// </summary>
		private List<byte> storedData;

		/// <summary>
		/// Triggers when a read operation has finished, and data is available for use
		/// </summary>
		public event DataReceivedCallback dataRead;
		#endregion

		#region STATIC_METHODS

		#endregion

		#region INSTANCE_METHODS

		/// <summary>
		/// Create a new Client object that needs to be connected to a remote endpoint
		/// </summary>
		/// <param name="address">The address to which connection wiil be attempted</param>
		/// <param name="port">The port, what else?</param>
		public Endpoint(IPAddress address, int port = DEFAULT_PORT) : this()
		{
			this.address = address;
			this.port = port;

			client = new TcpClient ();
			netStream = null;
		}

		/// <summary>
		/// Create a new Client object with an existing and connected TcpClient
		/// </summary>
		/// <param name="existingClient"></param>
		public Endpoint(TcpClient existingClient) : this()
		{
			address = null;
			port = -1;

			client = existingClient;
			netStream = client.GetStream();
			secureStream = new SslStream (netStream, false, ValidateConnectionCertificate);
			secureStream.AuthenticateAsServer (null /* TODO server cert */, true, true);
		}

		private Endpoint()
		{
			readingCount = 0;
			sendingCount = 0;
			storedData = new List<byte> ();
		}

		public void Start()
		{
			if (!Connected)
			{
				try
				{
					//establish connection
					client.Connect (address, port);
					netStream = client.GetStream ();
					secureStream = new SslStream (netStream, false, ValidateConnectionCertificate);
					secureStream.AuthenticateAsClient (address.ToString (), null /* TODO client certs */, true);
				}
				catch (Exception e) when (e is AuthenticationException || e is InvalidOperationException)
				{
					//failed connection for some reason
					Console.ForegroundColor = ConsoleColor.Red;
					Console.Error.WriteLine ("Could not connect to " + address.ToString () + ":" + port);
					Console.Error.WriteLine ("Cause: " + e.GetType().Name);
					Console.ResetColor ();
					Disconnected = true;
					return;
				}
#if DEBUG
				Console.WriteLine (DEBUG_TAG + " Sucessfully connected to " + address + ":" + port);
#endif
			}
		}

		/// <summary>
		/// If the Client is not connected to an endpoint, try connecting.
		/// This function is async.
		/// </summary>
		public void StartAsync()
		{
			if (!Connected)
			{
#if DEBUG
				Console.WriteLine (DEBUG_TAG + " Starting async connect");
#endif
				new Thread (Start).Start ();
			}
		}

		private bool ValidateConnectionCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors sslPolicyErrors)
		{
#if DEBUG
			Console.WriteLine (DEBUG_TAG + " Connection request (Issuer: " + cert.Issuer + " Subject: " + cert.Subject + ")");
#endif

			if (sslPolicyErrors == SslPolicyErrors.None)
				return true;

#if DEBUG
			Console.WriteLine (DEBUG_TAG + " Rejected connection request");
#endif

			return false;
		}

		public bool HasPendingData()
		{
			return netStream != null && netStream.DataAvailable;
		}

		/// <summary>
		/// Blocking read from the currently open connection
		/// </summary>
		/// <returns></returns>
		public Packet[] Read()
		{
			if (HasPendingData() && (Flow & FlowState.incoming) == FlowState.incoming)
			{
#if DEBUG
				Console.WriteLine (DEBUG_TAG + " Beginning read");
#endif
				Reading = true;

				byte[] dataBuffer = new byte[DB_LENGTH];
				int byteC;
				while (netStream.DataAvailable)
				{
					byteC = netStream.Read (dataBuffer, 0, dataBuffer.Length);
					for (int i = 0; i < byteC; i++)
					{
						storedData.Add (dataBuffer[i]);
					}
				}

				Reading = false;

				Packet[] packets;
				int numRead = Packet.DeserializeAll (storedData.ToArray (), out packets);
				int leftOver = storedData.Count - numRead;
				storedData.Clear ();

#if DEBUG
				Console.WriteLine (DEBUG_TAG + " Finished read (read: " + numRead + "B, leftover: " + leftOver + "B)");
#endif
				return packets;
			}
			return null;
		}

		/// <summary>
		/// Read from the currently open connection asynchronously
		/// </summary>
		public void ReadAsync()
		{
			if ((Flow & FlowState.incoming) == FlowState.incoming)
			{
				//TODO thread pooling?
				new Thread (() => {
#if DEBUG
					Console.WriteLine (DEBUG_TAG + " Started read thread");
#endif
					try
					{
						while (!HasPendingData ()) { }

						Packet[] packets = Read ();

						if (dataRead != null)
						{
#if DEBUG
							Console.WriteLine (DEBUG_TAG + " Notifying listeners of new data (" + packets.Length + " packets)");
#endif
							for (int i = 0; i < packets.Length; i++)
							{
								dataRead (this, packets[i]);
							}
						}
					}
					catch (Exception e)
					{
						Console.Error.WriteLine (e.ToString ());
					}
				}).Start ();
			}
		}

		/// <summary>
		/// Blocking send over the open connection
		/// </summary>
		/// <param name="packet"></param>
		public void Send(Packet packet)
		{
			if (netStream != null && (Flow & FlowState.outgoing) == FlowState.outgoing)
			{
				Sending = true;
#if DEBUG
				Console.WriteLine (DEBUG_TAG + " Sending \"" + packet.Data + "\"");
#endif
				//convert packet into binary data
				byte[] sendBytes = packet.ToBytes ();

				//begin send operation
				netStream.Write (sendBytes, 0, sendBytes.Length);

				Sending = false;
#if DEBUG
				Console.WriteLine (DEBUG_TAG + " Finished sending");
#endif
			}
		}

		/// <summary>
		/// Send data over the open connection asynchronously
		/// </summary>
		/// <param name="packet"></param>
		public void SendAsync(Packet packet)
		{
			if (netStream != null && (Flow & FlowState.outgoing) == FlowState.outgoing)
			{
#if DEBUG
				Console.WriteLine(DEBUG_TAG + " Sending \"" + packet.Data + "\"");
#endif
				//convert packet into binary data
				byte[] sendBytes = packet.ToBytes ();

				//begin send operation
				netStream.BeginWrite(sendBytes, 0, sendBytes.Length, SendAsyncFinished, netStream);

				Sending = true;
			}
		}

		private void SendAsyncFinished(IAsyncResult res)
		{
			NetworkStream stream = (NetworkStream)res.AsyncState;
			stream.EndWrite (res);

			Sending = false;
#if DEBUG
			Console.WriteLine (DEBUG_TAG + " Finished sending");
#endif
		}

		/// <summary>
		/// Perform a Send and block for the response
		/// </summary>
		/// <param name="outbound"></param>
		/// <returns></returns>
		public bool SendAndRead(Packet outbound, out Packet[] inbound)
		{
			Send(outbound);
			while (!netStream.DataAvailable) { }
			inbound = Read ();

			return true;
		}
		public bool SendAndWait(Packet outbound)
		{
			Packet[] inbound;
			return SendAndRead(outbound, out inbound);
		}

		public void Disconnect()
		{
			secureStream.Close ();
			netStream.Close();
			client.Close();
			Sending = Reading = false;
			Disconnected = true;
			Flow = FlowState.none;
#if DEBUG
			Console.WriteLine (DEBUG_TAG + " Disconnected from " + address.ToString() + ":" + port);
#endif
		}

		public override int GetHashCode()
		{
			return Name.GetHashCode();
		}

		public override bool Equals(object obj)
		{
			return Name.Equals(((Endpoint)obj).Name);
		}

		#region INTERFACE_METHODS
		public void Write(Message msg)
		{
			SendAsync (new Packet (Packet.DataType.logentry, msg));
		}

		public string ReadLine()
		{
			//TODO signal to the remote host that input is expected

			//wait for data
			Flow = FlowState.incoming;
			Packet p = Read ()[0];

			return p.Data;
		}
		#endregion
		#endregion

		#region INTERNAL_TYPES

		public delegate void DataReceivedCallback(Endpoint endpoint, Packet data);

		/// <summary>
		/// Indicates the type of traffic this endpoint will route.
		/// </summary>
		[Flags]
		public enum FlowState
		{
			/// <summary>
			/// All data will be ignored.
			/// </summary>
			none = 0x0,

			/// <summary>
			/// Only send data.
			/// </summary>
			outgoing = 0x1,

			/// <summary>
			/// Only recieve data.
			/// </summary>
			incoming = 0x2,

			/// <summary>
			/// Send and recieve data.
			/// </summary>
			bidirectional = outgoing | incoming
		}
		#endregion
	}
}
