import { Component, ElementRef, inject, OnDestroy, signal, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
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
export class AskComponent implements OnDestroy {
  @ViewChild('messagesEnd') private messagesEnd!: ElementRef;
  private static readonly CopyFeedbackMs = 1400;

  private api = inject(ApiService);

  question = signal('');
  messages = signal<Message[]>([]);
  streaming = signal(false);
  copiedMessageIndex = signal<number | null>(null);
  readonly suggestedQuestions = [
    'What are the main entry points in this codebase?',
    'How does data flow from an API endpoint to persistence?',
    'Which classes or services depend on a given component?'
  ];
  private abortController: AbortController | null = null;
  private copyFeedbackTimeoutId: number | null = null;

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
      for await (const event of this.api.ask(q, this.abortController.signal, undefined, history.length ? history : undefined)) {
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
    }
  }

  stop() {
    this.abortController?.abort();
  }

  startNewChat() {
    if (this.streaming()) {
      return;
    }

    this.messages.set([]);
    this.question.set('');
    this.copiedMessageIndex.set(null);
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
}
