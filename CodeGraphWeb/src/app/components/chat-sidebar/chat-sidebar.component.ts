import { Component, inject, signal, ElementRef, ViewChild, AfterViewChecked } from '@angular/core';
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
export class ChatSidebarComponent implements AfterViewChecked {
  @ViewChild('messagesEnd') private messagesEnd!: ElementRef;

  private api = inject(ApiService);
  chatContext = inject(ChatContextService);

  open = signal(false);
  question = signal('');
  messages = signal<Message[]>([]);
  streaming = signal(false);
  private abortController: AbortController | null = null;
  private shouldScrollToBottom = false;

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
      for await (const event of this.api.ask(q, this.abortController.signal, ctx?.apiContext, history.length ? history : undefined)) {
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
    }
  }

  stop() {
    this.abortController?.abort();
  }

  clear() {
    if (this.streaming()) this.stop();
    this.messages.set([]);
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
}
