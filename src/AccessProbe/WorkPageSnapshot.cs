using System.Text;
using System.Text.Json;

namespace AccessProbe;

public sealed record WorkPageSnapshot(
    string Outcome,
    WorkListParseResult? List,
    WorkDetailParseResult? Detail,
    bool RowLimitReached)
{
    [System.Text.Json.Serialization.JsonIgnore] public WorkTaskLocation? DetailLocation { get; init; }
}

public static class WorkPageSnapshotReader
{
    // Read metadata from the currently loaded document. No fetch, click, form or storage operation.
    // Detached nodes copy only the fields used by the parsers, never the whole page or answer content.
    public const string Script = """
        (() => {
            if (location.protocol !== 'https:' || location.hostname !== 'mooc1.chaoxing.com'
                || (location.port !== '' && location.port !== '443')) return { kind: 'unsupported_page' };
            if (document.readyState === 'loading') return { kind: 'page_loading' };
            const visible = node => node.getClientRects().length > 0;
            const make = (tag, className, text) => {
                const node = document.createElement(tag);
                if (className) node.className = className;
                if (text !== undefined) node.textContent = text;
                return node;
            };
            const rows = Array.from(document.querySelectorAll('li[data][onclick]'))
                .filter(row => /^\s*goTask\s*\(\s*this\s*\)\s*;?\s*$/.test(row.getAttribute('onclick') || '') && visible(row));
            const root = make('div');
            let kind;
            if (rows.length) {
                kind = 'list';
                for (const row of rows.slice(0, 500)) {
                    const item = make('li');
                    item.setAttribute('onclick', row.getAttribute('onclick') || '');
                    item.setAttribute('data', row.getAttribute('data') || '');
                    const content = make('div', 'right-content');
                    for (const [selector, className] of [['.right-content p.overHidden2', 'overHidden2'], ['.right-content p.status', 'status']]) {
                        const value = row.querySelector(selector);
                        if (value) content.appendChild(make('p', className, value.textContent));
                    }
                    item.appendChild(content);
                    const time = row.querySelector('.time');
                    if (time) item.appendChild(make('div', 'time', time.textContent));
                    root.appendChild(item);
                }
            } else {
                const headers = Array.from(document.querySelectorAll('.detailsHead')).filter(visible);
                if (!headers.length) return { kind: 'not_found' };
                if (headers.length !== 1) return { kind: 'ambiguous_detail' };
                kind = 'detail';
                for (const header of headers) {
                    const copy = make('div', 'detailsHead');
                    const title = header.querySelector('h2.mark_title');
                    if (title) copy.appendChild(make('h2', 'mark_title', title.textContent));
                    const info = make('div', 'infoHead');
                    for (const time of header.querySelectorAll('.infoHead p.rightBord')) {
                        if (!/^\s*作答时间\s*[:：]/.test(time.textContent || '')) continue;
                        // Only the displayed answer-window label and its endpoint text are copied.
                        const paragraph = make('p', 'rightBord');
                        for (const child of time.childNodes) {
                            if (child.nodeType === 3) paragraph.appendChild(document.createTextNode(child.textContent));
                            else if (child.nodeType === 1 && child.tagName === 'EM') paragraph.appendChild(make('em', '', child.textContent));
                            else paragraph.appendChild(document.createTextNode(child.textContent || ''));
                        }
                        info.appendChild(paragraph);
                    }
                    copy.appendChild(info);
                    root.appendChild(copy);
                }
            }
            const markup = root.innerHTML;
            if (new TextEncoder().encode(markup).length > 2097152) return { kind: 'response_too_large' };
            return { kind, markup, rowLimitReached: rows.length > 500, taskAddress: kind === 'detail' ? location.href : undefined };
        })()
        """;

    public static WorkPageSnapshot Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 4 * WorkListParser.MaximumBytes)
            return Failure("response_too_large");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String) return Failure("snapshot_unrecognized");
            var kind = kindElement.GetString();
            if (kind is "unsupported_page" or "page_loading" or "not_found" or "response_too_large" or "ambiguous_detail")
                return Failure(kind);
            if (kind is not ("list" or "detail")
                || !root.TryGetProperty("markup", out var markupElement)
                || markupElement.ValueKind != JsonValueKind.String) return Failure("snapshot_unrecognized");
            var markup = markupElement.GetString()!;
            var limited = root.TryGetProperty("rowLimitReached", out var rowLimit) && rowLimit.ValueKind == JsonValueKind.True;
            if (kind == "list")
            {
                var list = WorkListParser.Parse(markup);
                return new(list.Outcome, list, null, limited);
            }
            var detail = WorkDetailParser.Parse(markup);
            return new WorkPageSnapshot(detail.Outcome, null, detail, false)
            {
                DetailLocation = root.TryGetProperty("taskAddress", out var address) && address.ValueKind == JsonValueKind.String
                    ? WorkListParser.ParseTaskLocation(address.GetString()) : null
            };
        }
        catch (JsonException) { return Failure("snapshot_unrecognized"); }
    }

    private static WorkPageSnapshot Failure(string outcome) => new(outcome, null, null, false);
}
