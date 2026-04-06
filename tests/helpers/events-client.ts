import { getBaseUrl } from './api.js';

export interface ServerEvent {
  type: string;
  data: Record<string, unknown>;
}

type EventHandler = (event: ServerEvent) => void;

export class EventsClient {
  private handlers: EventHandler[] = [];
  private closeFunc: (() => void) | null = null;

  async connect(filter?: { supplier_id?: string }): Promise<void> {
    const baseUrl = getBaseUrl();

    // Try WebSocket first
    try {
      await this.tryWebSocket(baseUrl, filter);
      return;
    } catch {
      // WS not available, try SSE
    }

    // Try SSE
    try {
      await this.trySSE(baseUrl, filter);
      return;
    } catch {
      throw new Error('Neither WebSocket nor SSE available at /api/events');
    }
  }

  private tryWebSocket(baseUrl: string, filter?: { supplier_id?: string }): Promise<void> {
    return new Promise((resolve, reject) => {
      const wsUrl = baseUrl.replace(/^http/, 'ws');
      const params = filter?.supplier_id ? `?supplier_id=${filter.supplier_id}` : '';

      import('ws').then(({ default: WebSocket }) => {
        const ws = new WebSocket(`${wsUrl}/api/events${params}`);

        const timeout = setTimeout(() => {
          ws.close();
          reject(new Error('WebSocket connection timeout'));
        }, 3000);

        ws.on('open', () => {
          clearTimeout(timeout);
          this.closeFunc = () => ws.close();
          resolve();
        });

        ws.on('message', (data: Buffer) => {
          try {
            const event = JSON.parse(data.toString()) as ServerEvent;
            for (const handler of this.handlers) {
              handler(event);
            }
          } catch { /* ignore parse errors */ }
        });

        ws.on('error', () => {
          clearTimeout(timeout);
          reject(new Error('WebSocket connection failed'));
        });
      }).catch(reject);
    });
  }

  private trySSE(baseUrl: string, filter?: { supplier_id?: string }): Promise<void> {
    return new Promise(async (resolve, reject) => {
      const params = filter?.supplier_id ? `?supplier_id=${filter.supplier_id}` : '';
      const url = `${baseUrl}/api/events${params}`;

      try {
        const controller = new AbortController();
        const res = await fetch(url, {
          headers: { Accept: 'text/event-stream' },
          signal: controller.signal,
        });

        if (!res.ok || !res.body) {
          reject(new Error('SSE connection failed'));
          return;
        }

        this.closeFunc = () => controller.abort();

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        const readLoop = async () => {
          try {
            while (true) {
              const { done, value } = await reader.read();
              if (done) break;
              buffer += decoder.decode(value, { stream: true });
              const lines = buffer.split('\n');
              buffer = lines.pop() || '';
              for (const line of lines) {
                if (line.startsWith('data: ')) {
                  try {
                    const event = JSON.parse(line.slice(6)) as ServerEvent;
                    for (const handler of this.handlers) handler(event);
                  } catch { /* ignore */ }
                }
              }
            }
          } catch { /* stream closed */ }
        };

        readLoop();
        resolve();
      } catch (e) {
        reject(e);
      }
    });
  }

  onEvent(handler: EventHandler): void {
    this.handlers.push(handler);
  }

  waitForEvent(type: string, timeoutMs: number = 5000): Promise<ServerEvent> {
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        reject(new Error(`Timed out waiting for event "${type}" after ${timeoutMs}ms`));
      }, timeoutMs);

      this.onEvent((event) => {
        if (event.type === type) {
          clearTimeout(timeout);
          resolve(event);
        }
      });
    });
  }

  disconnect(): void {
    if (this.closeFunc) {
      this.closeFunc();
      this.closeFunc = null;
    }
    this.handlers = [];
  }
}
