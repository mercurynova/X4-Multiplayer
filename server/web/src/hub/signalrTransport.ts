import { HttpTransportType, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { HUB_URL } from './contract';
import type { HubTransport } from './HubManager';

/**
 * The real transport. Automatic reconnect is deliberately NOT enabled on the SignalR connection: the HubManager does its
 * own backoff loop and builds a fresh connection per attempt (the server drops group membership on disconnect anyway), which
 * keeps one tested code path for "re-subscribe after reconnect" and gives us unlimited retries.
 */
export function createSignalRTransport(url: string = HUB_URL): HubTransport {
  const connection = new HubConnectionBuilder()
    .withUrl(url, { transport: HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling })
    .configureLogging(LogLevel.Warning)
    .build();
  return {
    start: () => connection.start(),
    stop: () => connection.stop(),
    invoke: (method, ...args) => connection.invoke(method, ...args),
    on: (event, handler) => connection.on(event, handler),
    onClose: (handler) => connection.onclose(handler),
  };
}
