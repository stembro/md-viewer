namespace MarkdownViewer.Rendering;

internal static class DocumentStyles
{
    internal const string Css = """
        :root {
          color-scheme: light;
          --paper: #fffefa;
          --ink: #292d32;
          --muted: #73766f;
          --line: #e5e5dc;
          --soft: #f4f4ee;
          --accent: #2d6b59;
          --accent-soft: #e8f1eb;
          --code: #a54735;
        }
        :root[data-theme="dark"] {
          color-scheme: dark;
          --paper: #202421;
          --ink: #e5e9e3;
          --muted: #a0aaa0;
          --line: #3a423b;
          --soft: #292f2a;
          --accent: #8dceb1;
          --accent-soft: #334a3c;
          --code: #e7aa90;
        }
        * { box-sizing: border-box; }
        html { background: var(--paper); scroll-padding-top: 30px; }
        body {
          margin: 0;
          color: var(--ink);
          background: var(--paper);
          font-family: "Segoe UI", system-ui, sans-serif;
          font-size: 16px;
          line-height: 1.75;
          text-rendering: optimizeLegibility;
          -webkit-font-smoothing: antialiased;
          overflow-wrap: break-word;
        }
        .markdown-body { max-width: 896px; padding: 52px 48px 100px; margin: 0 auto; }
        .markdown-body > :first-child { margin-top: 0; }
        .markdown-body > :last-child { margin-bottom: 0; }
        h1, h2, h3, h4, h5, h6 {
          color: var(--ink);
          font-weight: 600;
          line-height: 1.3;
          scroll-margin-top: 30px;
          overflow-wrap: anywhere;
        }
        h1, h2 { font-family: Georgia, "Times New Roman", serif; font-weight: 400; letter-spacing: -.025em; }
        h1 { font-size: 2.55em; line-height: 1.2; margin: 1.25em 0 .65em; }
        h2 { font-size: 1.8em; margin: 1.65em 0 .65em; padding-bottom: .35em; border-bottom: 1px solid var(--line); }
        h3 { font-size: 1.22em; margin: 1.6em 0 .6em; }
        h4 { font-size: 1.05em; margin: 1.5em 0 .5em; }
        h5, h6 { font-size: 1em; margin: 1.5em 0 .5em; }
        h6 { color: var(--muted); }
        p, ul, ol, dl, pre, table, blockquote { margin: 0 0 1.25em; }
        a { color: var(--accent); text-underline-offset: .2em; text-decoration-thickness: 1px; }
        a:hover { text-decoration-thickness: 2px; }
        a:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; border-radius: 2px; }
        strong { font-weight: 650; }
        del { color: var(--muted); }
        ul, ol { padding-left: 1.65em; }
        li { padding-left: .2em; margin: .3em 0; }
        li > p { margin: .35em 0; }
        li > ul, li > ol { margin: .4em 0; }
        li::marker { color: var(--muted); }
        .task-list-item { list-style: none; }
        .task-list-item input, input[type="checkbox"] {
          accent-color: var(--accent);
          margin: 0 .65em 0 -1.45em;
          width: 14px;
          height: 14px;
          vertical-align: -.05em;
        }
        blockquote {
          border-left: 3px solid var(--accent);
          background: var(--soft);
          padding: .85em 1.2em;
          border-radius: 0 6px 6px 0;
          color: var(--muted);
        }
        blockquote > :last-child { margin-bottom: 0; }
        code, pre, kbd, samp { font-family: "Cascadia Code", "Cascadia Mono", Consolas, monospace; font-size: .875em; }
        code { padding: .16em .38em; background: var(--soft); color: var(--code); border-radius: 4px; }
        pre {
          background: var(--soft);
          border: 1px solid var(--line);
          border-radius: 8px;
          padding: 18px 22px;
          line-height: 1.65;
          overflow-x: auto;
          tab-size: 4;
          overflow-wrap: normal;
        }
        pre code { padding: 0; color: var(--ink); background: transparent; border-radius: 0; font-size: inherit; }
        table { border-collapse: collapse; display: block; width: max-content; max-width: 100%; overflow-x: auto; font-size: .94em; }
        th, td { padding: .65em 1em; border: 1px solid var(--line); text-align: left; }
        th { font-weight: 600; background: var(--soft); }
        tr:nth-child(even) td { background: color-mix(in srgb, var(--soft) 55%, transparent); }
        img { max-width: 100%; height: auto; border-radius: 5px; vertical-align: middle; }
        hr { border: 0; border-top: 1px solid var(--line); margin: 2.5em 0; }
        ::selection { color: var(--ink); background: var(--accent-soft); }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: var(--paper); }
        ::-webkit-scrollbar-thumb { background: var(--line); border: 2px solid var(--paper); border-radius: 8px; }
        @media (max-width: 600px) {
          .markdown-body { padding: 28px 24px 64px; }
          h1 { font-size: 2.1em; }
          h2 { font-size: 1.6em; }
          pre { padding: 14px 16px; }
        }
        @media print {
          :root, :root[data-theme="dark"] {
            color-scheme: light;
            --paper: #fff;
            --ink: #171717;
            --muted: #555;
            --line: #ddd;
            --soft: #f5f5f5;
            --accent: #235440;
            --code: #333;
          }
          body { font-size: 11pt; }
          .markdown-body { max-width: none; padding: 0; }
          h1, h2, h3, h4, h5, h6 { break-after: avoid; }
          pre, blockquote, img, tr { break-inside: avoid; }
          pre { white-space: pre-wrap; overflow-wrap: anywhere; }
          table { display: table; width: 100%; overflow: visible; }
          a { color: inherit; }
          @page { margin: 20mm; }
        }
        """;
}
