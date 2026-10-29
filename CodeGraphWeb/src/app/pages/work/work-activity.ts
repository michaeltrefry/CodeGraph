import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { changeLines, changeVerb } from './work-format';
import { ActorComponent, AgoPipe } from './work-shared';
import { WorkChange } from './work.models';

/** Recent writes from the change feed, newest first, each with who made it. */
@Component({
  selector: 'wrk-activity',
  standalone: true,
  imports: [RouterLink, ActorComponent, AgoPipe],
  template: `
    @if (recent().length === 0) { <div class="wrk-empty">No activity yet.</div> }
    <ul class="wrk-list">
      @for (change of recent(); track change.sequence) {
        <li>
          <div class="wrk-row">
            <wrk-actor [actor]="change.actor" />
            <span>{{ verb(change) }}</span>
            @if (change.entity === 'item') { <a [routerLink]="['/work/items', change.entity_key]">{{ change.entity_key }}</a> }
            @else if (change.entity === 'feature') { <a [routerLink]="['/work/features', change.entity_key]">{{ change.entity_key }}</a> }
            @else { <span class="wrk-key">{{ change.entity_key }}</span> }
            <span class="wrk-sub wrk-grow" style="text-align:right">{{ change.created_at | ago }}</span>
          </div>
          @if (lines(change).length > 0) {
            <div class="wrk-diff">
              @for (line of lines(change); track line.field) {
                <div>{{ line.field }}: <del>{{ line.old }}</del> <ins>{{ line.new }}</ins></div>
              }
            </div>
          }
        </li>
      }
    </ul>
  `
})
export class ActivityComponent {
  readonly entries = input.required<WorkChange[]>();
  readonly limit = input(25);
  readonly recent = computed(() => [...this.entries()].reverse().slice(0, this.limit()));
  verb = changeVerb;
  lines = (change: WorkChange) => (change.kind.endsWith('_created') ? [] : changeLines(change));
}
