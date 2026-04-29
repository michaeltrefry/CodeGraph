import { AfterViewChecked, Component, ElementRef, inject, OnDestroy, OnInit, signal, ViewChild } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';

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
  selector: 'app-chat-sidebar',
  templateUrl: './chat-sidebar.component.html',
  styleUrl: './chat-sidebar.component.scss'
})
export class ChatSidebarComponent implements AfterViewChecked, OnInit, OnDestroy {
  @ViewChild('messagesEnd') private messagesEnd!: ElementRef;
  private static readonly ActiveChatStorageKey = 'codegraph:sidebar-chat:active-chat-id';
  private static readonly NewChatSentinel = '__new__';

  private api = inject(ApiService);
  chatContext = inject(ChatContextService);

  open = signal(false);
  question = signal('');
  messages = signal<Message[]>([]);
  streaming = signal(false);
  activeChatId = signal<string | null>(null);
  private abortController: AbortController | null = null;
  private activeRunId: number | null = null;
  private shouldScrollToBottom = false;

  ngOnInit() {
    this.loadInitialChat();
  }

  ngOnDestroy() {
    this.abortController?.abort();
  }

  ngAfterViewChecked() {
    if (this.shouldScrollToBottom) {
      this.scrollToBottom();
      this.shouldScrollToBottom = false;
    }
  }

  toggle() {
    this.open.update(v => !v);
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
    this.shouldScrollToBottom = true;

    this.abortController = new AbortController();
    const ctx = this.chatContext.context();

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

    try {
      const run = await firstValueFrom(this.api.startAssistantRun({
        question: q,
        context: ctx?.apiContext,
        history: history.length ? history : undefined,
        chatId: this.activeChatId() ?? undefined
      }));

      this.activeChatId.set(run.chatId);
      this.activeRunId = run.runId;
      this.saveActiveChatId(run.chatId);

      for await (const event of this.api.streamAssistantRun(run.runId, 0, this.abortController.signal)) {
        if (event.type === 'text') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            const lastChunk = last.chunks[last.chunks.length - 1];
            if (lastChunk && !lastChunk.isToolUse) {
              lastChunk.text += event.content;
            } else {
              last.chunks.push({ text: event.content });
            }
            return [...msgs];
          });
          this.shouldScrollToBottom = true;
        } else if (event.type === 'tool_use') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            last.toolsUsed.push(event.content);
            last.chunks.push({ text: event.content, isToolUse: true });
            return [...msgs];
          });
          this.shouldScrollToBottom = true;
        } else if (event.type === 'done') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            last.done = true;
            return [...msgs];
          });
          break;
        } else if (event.type === 'error') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            last.error = event.content;
            last.done = true;
            return [...msgs];
          });
          break;
        }
      }
    } catch (err: any) {
      if (err?.name !== 'AbortError') {
        this.messages.update(msgs => {
          const last = msgs[msgs.length - 1];
          last.error = String(err);
          last.done = true;
          return [...msgs];
        });
      }
    } finally {
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
    this.saveActiveChatId(ChatSidebarComponent.NewChatSentinel);
  }

  onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  private scrollToBottom() {
    try {
      this.messagesEnd?.nativeElement?.scrollIntoView({ behavior: 'smooth' });
    } catch { /* ignore */ }
  }

  renderMarkdown(text: string): string {
    return text
      .replace(/```([\s\S]*?)```/g, (_m, code) => `<pre><code>${this.esc(code)}</code></pre>`)
      .replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>')
      .replace(/`([^`\n]+)`/g, '<code>$1</code>')
      .replace(/^### (.+)$/gm, '<h4>$1</h4>')
      .replace(/^## (.+)$/gm, '<h3>$1</h3>')
      .replace(/^# (.+)$/gm, '<h2>$1</h2>')
      .replace(/^- (.+)$/gm, '<li>$1</li>')
      .replace(/\n\n/g, '<br><br>')
      .replace(/\n/g, '<br>');
  }

  private esc(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  private loadInitialChat() {
    const stored = this.readActiveChatId();
    if (stored === ChatSidebarComponent.NewChatSentinel) {
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

        this.shouldScrollToBottom = true;
      },
      error: () => {
        this.activeChatId.set(null);
      }
    });
  }

  private async watchRun(runId: number, afterSequence: number) {
    try {
      for await (const event of this.api.streamAssistantRun(runId, afterSequence, this.abortController?.signal)) {
        if (event.type === 'text') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            const lastChunk = last.chunks[last.chunks.length - 1];
            if (lastChunk && !lastChunk.isToolUse) {
              lastChunk.text += event.content;
            } else {
              last.chunks.push({ text: event.content });
            }
            return [...msgs];
          });
          this.shouldScrollToBottom = true;
        } else if (event.type === 'tool_use') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            last.toolsUsed.push(event.content);
            last.chunks.push({ text: event.content, isToolUse: true });
            return [...msgs];
          });
          this.shouldScrollToBottom = true;
        } else if (event.type === 'done') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            last.done = true;
            return [...msgs];
          });
          break;
        } else if (event.type === 'error') {
          this.messages.update(msgs => {
            const last = msgs[msgs.length - 1];
            last.error = event.content;
            last.done = true;
            return [...msgs];
          });
          break;
        }
      }
    } catch (err: any) {
      if (err?.name !== 'AbortError') {
        this.messages.update(msgs => {
          const last = msgs[msgs.length - 1];
          last.error = String(err);
          last.done = true;
          return [...msgs];
        });
      }
    } finally {
      this.streaming.set(false);
      this.abortController = null;
      this.activeRunId = null;
    }
  }

  private readActiveChatId(): string | null {
    try {
      return window.localStorage.getItem(ChatSidebarComponent.ActiveChatStorageKey);
    } catch {
      return null;
    }
  }

  private saveActiveChatId(chatId: string) {
    try {
      window.localStorage.setItem(ChatSidebarComponent.ActiveChatStorageKey, chatId);
    } catch {}
  }
}
