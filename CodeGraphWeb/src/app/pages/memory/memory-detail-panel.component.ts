import { DatePipe } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { MemoryClaim, MemoryClaimBundle, MemoryEntity, MemoryEntityBundle } from '../../core/models';

@Component({
  selector: 'app-memory-detail-panel',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './memory-detail-panel.component.html',
  styleUrl: './memory-detail-panel.component.scss'
})
export class MemoryDetailPanelComponent {
  @Input() loadingDetail = false;
  @Input() focusMode = false;
  @Input() selectedBundle: MemoryEntityBundle | null = null;
  @Input() selectedClaimBundle: MemoryClaimBundle | null = null;
  @Input() nodeLookup: ReadonlyMap<string, MemoryEntity> = new Map();
  @Input() neighborRows: Array<{ id: string; label: string; type: string; edgeType: string; updatedAt?: string }> = [];

  @Output() focusEntity = new EventEmitter<string>();
  @Output() inspectClaim = new EventEmitter<string>();
  @Output() showOverview = new EventEmitter<void>();

  formatType(value: string): string {
    return value
      .replace(/[_-]+/g, ' ')
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/\b\w/g, char => char.toUpperCase());
  }

  formatClaim(claim: MemoryClaim): string {
    const subject = this.entityName(claim.subjectEntityId);
    const object = claim.objectEntityId ? this.entityName(claim.objectEntityId) : null;
    const value = claim.valueText?.trim();
    return [subject, claim.predicate, object ?? value].filter(Boolean).join(' ');
  }

  private entityName(entityId: string): string {
    return this.nodeLookup.get(entityId)?.label
      ?? (this.selectedBundle?.entity.id === entityId ? this.selectedBundle.entity.label : entityId);
  }
}
