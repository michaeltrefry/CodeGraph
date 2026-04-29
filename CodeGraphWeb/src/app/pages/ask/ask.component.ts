import { Component, ElementRef, inject, OnDestroy, OnInit, signal, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { MarkdownComponent } from '../../shared/markdown.component';

interface MessageChunk {
  text: string;
  isToolUse?: boolean;
}

interface Message {
  role: 'user' | 'assistant';
  chunks: MessageChunk[];
  toolsUsed: string[];
  done: boolean;
  error?: string;
}

@Component({
  selector: 'app-ask',
  imports: [FormsModule, MarkdownComponent],
  templateUrl: './ask.component.html',
  styleUrl: './ask.component.scss'
})
export class AskComponent implements OnInit, OnDestroy {
  @ViewChild('messagesEnd') private messagesEnd!: ElementRef;
  private static readonly CopyFeedbackMs = 1400;
  private static readonly ActiveChatStorageKey = 'codegraph:ask:active-chat-id';
  private static readonly NewChatSentinel = '__new__';

  private api = inject(ApiService);

  question = signal('');
  messages = signal<Message[]>([]);
  streaming = signal(false);
  copiedMessageIndex = signal<number | null>(null);
  activeChatId = signal<string | null>(null);
  readonly suggestedQuestions = [
    'What are the main entry points in this codebase?',
    'How does data flow from an API endpoint to persistence?',
    'Which classes or services depend on a given component?'
  ];
  private abortController: AbortController | null = null;
  private copyFeedbackTimeoutId: number | null = null;
  private activeRunId: number | null = null;

  ngOnInit(): void {
    this.loadInitialChat();
  }

  ngOnDestroy(): void {
    this.abortController?.abort();
    this.clearCopyFeedbackTimeout();
  }

  async send() {
    const q = this.question().trim();
    if (!q || this.streaming()) return;

    this.messages.update(msgs => [...msgs, {
      role: 'user',
      chunks: [{ text: q }],
      toolsUsed: [],
      done: true
    }]);

    const assistantMsg: Message = {
      role: 'assistant',
      chunks: [],
      toolsUsed: [],
      done: false
    };

    this.messages.update(msgs => [...msgs, assistantMsg]);
    this.question.set('');
    this.streaming.set(true);
    this.scheduleScroll();

    this.abortController = new AbortController();

    // Build history from completed messages (exclude the current user + assistant pair)
    const allMsgs = this.messages();
    const history = allMsgs.slice(0, -2)
      .filter(m => m.done)
      .map(m => ({
        role: m.role,
        content: m.role === 'assistant'
          ? m.chunks.filter(c => !c.isToolUse).map(c => c.text).join('')
          : m.chunks.map(c => c.text).join('')
      }));

    let eventCount = 0;
    let textLength = 0;
    try {
      const run = await firstValueFrom(this.api.startAssistantRun({
        question: q,
        history: history.length ? history : undefined,
        chatId: this.activeChatId() ?? undefined
      }));

      this.activeChatId.set(run.chatId);
      this.activeRunId = run.runId;
      this.saveActiveChatId(run.chatId);

      for await (const event of this.api.streamAssistantRun(run.runId, 0, this.abortController.signal)) {
        eventCount++;
        if (event.type === 'text') {
          textLength += event.content.length;
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            const lastChunk = last.chunks[last.chunks.length - 1];
            const newChunks = lastChunk && !lastChunk.isToolUse
              ? [...last.chunks.slice(0, -1), { ...lastChunk, text: lastChunk.text + event.content }]
              : [...last.chunks, { text: event.content }];
            return [...msgs.slice(0, -1), { ...last, chunks: newChunks }];
          });
          this.scheduleScroll();
        } else if (event.type === 'tool_use') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            return [...msgs.slice(0, -1), {
              ...last,
              toolsUsed: [...last.toolsUsed, event.content],
              chunks: [...last.chunks, { text: event.content, isToolUse: true }]
            }];
          });
          this.scheduleScroll();
        } else if (event.type === 'done') {
          console.log(`[Ask] Stream done — ${eventCount} events, ${textLength} chars of text, chunks:`,
            this.messages().at(-1)?.chunks.length);
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            return [...msgs.slice(0, -1), { ...last, done: true }];
          });
          break;
        } else if (event.type === 'error') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            return [...msgs.slice(0, -1), { ...last, error: event.content, done: true }];
          });
          break;
        }
      }
    } catch (err: any) {
      console.error(`[Ask] Stream error after ${eventCount} events, ${textLength} text chars:`, err);
      if (err?.name !== 'AbortError') {
        this.messages.update(msgs => {
          const last = msgs[msgs.length - 1];
          return [...msgs.slice(0, -1), { ...last, error: String(err), done: true }];
        });
      }
    } finally {
      console.log(`[Ask] Stream finally — ${eventCount} events received, ${textLength} text chars`);
      this.streaming.set(false);
      this.abortController = null;
      this.activeRunId = null;
    }
  }

  stop() {
    this.abortController?.abort();
    if (this.activeRunId !== null) {
      this.api.cancelAssistantRun(this.activeRunId).subscribe({ error: () => {} });
    }
  }

  startNewChat() {
    if (this.streaming()) {
      return;
    }

    this.activeChatId.set(null);
    this.activeRunId = null;
    this.messages.set([]);
    this.question.set('');
    this.copiedMessageIndex.set(null);
    this.saveActiveChatId(AskComponent.NewChatSentinel);
  }

  onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  private scrollPending = false;

  private scheduleScroll() {
    if (this.scrollPending) return;
    this.scrollPending = true;
    requestAnimationFrame(() => {
      this.scrollPending = false;
      this.scrollToBottom();
    });
  }

  private scrollToBottom() {
    try {
      this.messagesEnd?.nativeElement?.scrollIntoView({ behavior: 'instant' });
    } catch { /* ignore */ }
  }

  fullText(msg: Message): string {
    return msg.chunks.filter(c => !c.isToolUse).map(c => c.text).join('');
  }

  formatToolCallName(value: string): string {
    const raw = value.trim();
    if (!raw) {
      return 'tool';
    }

    const firstLine = raw.split(/\r?\n/, 1)[0].trim();
    return firstLine.replace(/^mcp__codegraph__/, '');
  }

  async copyAssistantText(markdown: string, index: number): Promise<void> {
    const normalized = markdown.trim();
    if (!normalized) {
      return;
    }

    try {
      await navigator.clipboard.writeText(normalized);
      this.copiedMessageIndex.set(index);
      this.clearCopyFeedbackTimeout();
      this.copyFeedbackTimeoutId = window.setTimeout(() => {
        this.copiedMessageIndex.set(null);
        this.copyFeedbackTimeoutId = null;
      }, AskComponent.CopyFeedbackMs);
    } catch {
      this.copiedMessageIndex.set(null);
    }
  }

  private clearCopyFeedbackTimeout() {
    if (this.copyFeedbackTimeoutId !== null) {
      window.clearTimeout(this.copyFeedbackTimeoutId);
      this.copyFeedbackTimeoutId = null;
    }
  }

  private loadInitialChat() {
    const stored = this.readActiveChatId();
    if (stored === AskComponent.NewChatSentinel) {
      return;
    }

    if (stored) {
      this.loadChat(stored);
      return;
    }

    this.api.listAssistantChats(1).subscribe({
      next: chats => {
        const latest = chats[0];
        if (!latest) return;
        this.loadChat(latest.chatId);
      },
      error: () => {}
    });
  }

  private loadChat(chatId: string) {
    this.api.getAssistantChat(chatId).subscribe({
      next: chat => {
        this.activeChatId.set(chat.chatId);
        this.saveActiveChatId(chat.chatId);
        this.messages.set(chat.messages.map(message => ({
          role: message.role,
          chunks: [{ text: message.content }],
          toolsUsed: [],
          done: true
        })));

        if (chat.activeRun) {
          this.activeRunId = chat.activeRun.id;
          this.messages.update(messages => [...messages, {
            role: 'assistant',
            chunks: [],
            toolsUsed: [],
            done: false
          }]);
          this.streaming.set(true);
          this.abortController = new AbortController();
          void this.watchRun(chat.activeRun.id, 0);
        }

        this.scheduleScroll();
      },
      error: () => {
        this.activeChatId.set(null);
      }
    });
  }

  private async watchRun(runId: number, afterSequence: number) {
    let textLength = 0;
    try {
      for await (const event of this.api.streamAssistantRun(runId, afterSequence, this.abortController?.signal)) {
        if (event.type === 'text') {
          textLength += event.content.length;
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            const lastChunk = last.chunks[last.chunks.length - 1];
            const newChunks = lastChunk && !lastChunk.isToolUse
              ? [...last.chunks.slice(0, -1), { ...lastChunk, text: lastChunk.text + event.content }]
              : [...last.chunks, { text: event.content }];
            return [...msgs.slice(0, -1), { ...last, chunks: newChunks }];
          });
          this.scheduleScroll();
        } else if (event.type === 'tool_use') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            return [...msgs.slice(0, -1), {
              ...last,
              toolsUsed: [...last.toolsUsed, event.content],
              chunks: [...last.chunks, { text: event.content, isToolUse: true }]
            }];
          });
          this.scheduleScroll();
        } else if (event.type === 'done') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            return [...msgs.slice(0, -1), { ...last, done: true }];
          });
          break;
        } else if (event.type === 'error') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            return [...msgs.slice(0, -1), { ...last, error: event.content, done: true }];
          });
          break;
        }
      }
    } catch (err: any) {
      if (err?.name !== 'AbortError') {
        this.messages.update(msgs => {
          const last = msgs[msgs.length - 1];
          return [...msgs.slice(0, -1), { ...last, error: String(err), done: true }];
        });
      }
    } finally {
      console.log(`[Ask] Reconnected stream finished — ${textLength} text chars`);
      this.streaming.set(false);
      this.abortController = null;
      this.activeRunId = null;
    }
  }

  private readActiveChatId(): string | null {
    try {
      return window.localStorage.getItem(AskComponent.ActiveChatStorageKey);
    } catch {
      return null;
    }
  }

  private saveActiveChatId(chatId: string) {
    try {
      window.localStorage.setItem(AskComponent.ActiveChatStorageKey, chatId);
    } catch {}
  }
}
