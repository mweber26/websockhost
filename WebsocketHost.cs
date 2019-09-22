using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

//-----------------------------------------------------------------------------------------------------------------
//core ideas come from https://thomaslevesque.com/2015/06/04/async-and-cancellation-support-for-wait-handles/
//-----------------------------------------------------------------------------------------------------------------

namespace WebServer
{
	public class WebsocketHost
	{
		private readonly HttpContext _context;
		private readonly WebSocket _socket;
		private readonly CancellationToken _shutdown;
		//queue to hold pending data to send
		private readonly ConcurrentQueue<SendData> _queue = new ConcurrentQueue<SendData>();
		//set up an event so we know to immeditly send data
		private readonly AutoResetEvent _sendEvent = new AutoResetEvent(false);

		private class SendData
		{
			public string text;
			public byte[] binary;
		}

		public WebsocketHost(HttpContext context, WebSocket socket, CancellationToken shutdown)
		{
			_context = context;
			_socket = socket;
			_shutdown = shutdown;
		}

		public void Send(string msg)
		{
			//queue the data and set the event to wake up the sender task
			_queue.Enqueue(new SendData { text = msg });
			_sendEvent.Set();
		}

		public void Send(byte[] msg)
		{
			//queue the data and set the event to wake up the sender task
			_queue.Enqueue(new SendData { binary = msg });
			_sendEvent.Set();
		}

		public async Task Process(Action<byte[]> onMessage)
		{
			try
			{
				//create a cancel token for when we receive a directed close from the client
				var clientCancel = new CancellationTokenSource();
				//merge our client cancel with the server shutdown
				var anyCancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, clientCancel.Token).Token;
				//run push/receive tasks
				await Task.WhenAll(SendMessages(anyCancel), ReceiveMessages(onMessage, anyCancel, clientCancel));
				//then close the server
				await _socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Server is stopping", CancellationToken.None);
			}
			catch(OperationCanceledException)
			{
				//exit normally
			}
			catch(WebSocketException ex)
			{
				switch(ex.WebSocketErrorCode)
				{
					case WebSocketError.ConnectionClosedPrematurely:
						break;

					default:
						await _socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Server is stopping", CancellationToken.None);
						break;
				}
			}
		}

		private async Task SendMessages(CancellationToken cancelToken)
		{
			while(!cancelToken.IsCancellationRequested)
			{
				//wait for the send or timeout.  We have a timeout because if two sends come "simultanously" (or at lease close enough that we don't yet reset the auto reset event) then we could lose one of the signals.  If we don't have a timeout then everything will be out of sync.  So adding a small timeout will allow that second message to be send, then we can get back in sync with the autoreset event).
				await WaitOneAsync(_sendEvent, 100, cancelToken);

				//have a message to send?
				if(!_queue.TryDequeue(out var msg))
					continue;

				try
				{
					//send it
					if(msg.binary != null)
						await _socket.SendAsync(msg.binary, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken: cancelToken);
					else
						await _socket.SendAsync(Encoding.UTF8.GetBytes(msg.text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: cancelToken);
				}
				catch(OperationCanceledException)
				{
					//exit normally
				}
			}
		}

		private async Task ReceiveMessages(Action<byte[]> onReceive, CancellationToken cancelToken, CancellationTokenSource clientClose)
		{
			while(!cancelToken.IsCancellationRequested)
			{
				try
				{
					//pull an entire message
					var (response, message) = await ReceiveFullMessage(_socket, cancelToken);
					if(response.MessageType == WebSocketMessageType.Close)
					{
						//fire our client cancellation token
						clientClose.Cancel();
						break;
					}

					//process the data
					onReceive(message);
				}
				catch(OperationCanceledException)
				{
					//exit normally
				}
			}
		}

		//this continues to receive data from the websocket until we have a complete message
		private static async Task<(WebSocketReceiveResult, byte[])> ReceiveFullMessage(WebSocket socket, CancellationToken cancelToken)
		{
			WebSocketReceiveResult response;
			var message = new List<byte>();

			var buffer = new byte[4096];
			do
			{
				response = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancelToken);
				message.AddRange(new ArraySegment<byte>(buffer, 0, response.Count));
			} while(!response.EndOfMessage);

			return (response, message.ToArray());
		}

		//convert WaitOneHandle from sync to async (https://thomaslevesque.com/2015/06/04/async-and-cancellation-support-for-wait-handles/)
		private static async Task<bool> WaitOneAsync(WaitHandle handle, int millisecondsTimeout, CancellationToken cancellationToken)
		{
			RegisteredWaitHandle registeredHandle = null;
			var tokenRegistration = default(CancellationTokenRegistration);
			try
			{
				var tcs = new TaskCompletionSource<bool>();
				registeredHandle = ThreadPool.RegisterWaitForSingleObject(
					handle,
					(state, timedOut) => ((TaskCompletionSource<bool>)state).TrySetResult(!timedOut),
					tcs,
					millisecondsTimeout,
					true);
				tokenRegistration = cancellationToken.Register(
					state => ((TaskCompletionSource<bool>)state).TrySetCanceled(),
					tcs);
				return await tcs.Task;
			}
			finally
			{
				registeredHandle?.Unregister(null);
				tokenRegistration.Dispose();
			}
		}
	}
}
