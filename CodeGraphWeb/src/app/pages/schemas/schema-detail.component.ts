import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ChatContextService } from '../../core/chat-context.service';
import { SchemaCatalogComponent } from './schema-catalog.component';

@Component({
  selector: 'app-schema-detail',
  imports: [RouterLink, SchemaCatalogComponent],
  templateUrl: './schema-detail.component.html',
  styleUrl: './schema-detail.component.scss'
})
export class SchemaDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private chatContext = inject(ChatContextService);

  projectName = signal('');

  ngOnInit() {
    const name = this.route.snapshot.paramMap.get('name') ?? '';
    this.projectName.set(name);
    this.chatContext.setRepo(name);
  }
}
