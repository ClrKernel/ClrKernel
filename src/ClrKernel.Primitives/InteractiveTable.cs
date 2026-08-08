using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace ClrKernel.Primitives {
    /// <summary>
    /// Builds a self-contained interactive HTML grid (sort, filter, and an
    /// Analyze panel of per-column stats) for tabular data — the kind of output
    /// <c>DisplayTable()</c> produces. Everything (markup, CSS, JS, and the row
    /// data as embedded JSON) is emitted inline into a single container so it
    /// renders in a VS Code notebook <c>text/html</c> output, JupyterLab, or a
    /// plain browser without any external assets or a custom renderer. Colors
    /// come from VS Code theme variables with sensible fallbacks, so it blends
    /// into light and dark themes.
    /// </summary>
    public static class InteractiveTable {
        /// <summary>Column kinds the grid understands for sorting and stats.</summary>
        public const string Number = "number";
        public const string Date = "date";
        public const string Text = "string";

        /// <summary>
        /// Renders the grid. <paramref name="columns"/> are header labels;
        /// <paramref name="rows"/> holds already-stringified cell values (one
        /// inner list per row, aligned to <paramref name="columns"/>);
        /// <paramref name="types"/> gives each column's kind (<see cref="Number"/>,
        /// <see cref="Date"/>, or <see cref="Text"/>) for type-aware sorting and
        /// stats. <paramref name="totalRows"/> is the full row count before any
        /// limit so a truncated grid can say "showing first N of M".
        /// </summary>
        public static string Render(
            IReadOnlyList<string> columns,
            IReadOnlyList<IReadOnlyList<string>> rows,
            IReadOnlyList<string> types,
            int totalRows) {
            if (columns == null) {
                columns = Array.Empty<string>();
            }
            if (rows == null) {
                rows = Array.Empty<IReadOnlyList<string>>();
            }

            var id = "clrkernel-grid-" + Guid.NewGuid().ToString("N");
            var json = BuildJson(columns, rows, types, totalRows);

            var sb = new StringBuilder();
            sb.Append("<div class=\"clrkernel-table\" id=\"").Append(id).Append("\">");
            sb.Append(Style(id));
            sb.Append("<div class=\"ck-toolbar\">")
                .Append("<input class=\"ck-filter\" type=\"text\" placeholder=\"Filter rows…\" />")
                .Append("<button class=\"ck-analyze\" type=\"button\">Analyze</button>")
                .Append("<span class=\"ck-count\"></span>")
                .Append("</div>");
            sb.Append("<div class=\"ck-analyze-panel\" style=\"display:none\"></div>");
            sb.Append("<div class=\"ck-scroll\"><table><thead></thead><tbody></tbody></table></div>");

            // Data payload — parsed by the grid script. Escaping "</" keeps the
            // browser from ending the enclosing element early; it stays valid JSON
            // (\/ is just an escaped solidus).
            sb.Append("<script type=\"application/json\" class=\"ck-data\">")
                .Append(json.Replace("</", "<\\/"))
                .Append("</script>");
            sb.Append(Script(id));
            sb.Append("</div>");
            return sb.ToString();
        }

        // --- JSON payload (hand-rolled: no System.Text.Json in netstandard2.0) --

        private static string BuildJson(
            IReadOnlyList<string> columns,
            IReadOnlyList<IReadOnlyList<string>> rows,
            IReadOnlyList<string> types,
            int totalRows) {
            var sb = new StringBuilder();
            sb.Append("{\"cols\":[");
            for (var i = 0; i < columns.Count; i++) {
                if (i > 0) {
                    sb.Append(',');
                }
                sb.Append(JsonString(columns[i]));
            }
            sb.Append("],\"types\":[");
            for (var i = 0; i < columns.Count; i++) {
                if (i > 0) {
                    sb.Append(',');
                }
                var kind = types != null && i < types.Count ? types[i] : Text;
                sb.Append(JsonString(kind ?? Text));
            }
            sb.Append("],\"rows\":[");
            for (var r = 0; r < rows.Count; r++) {
                if (r > 0) {
                    sb.Append(',');
                }
                sb.Append('[');
                var row = rows[r];
                for (var c = 0; c < columns.Count; c++) {
                    if (c > 0) {
                        sb.Append(',');
                    }
                    var cell = row != null && c < row.Count ? row[c] : null;
                    sb.Append(JsonString(cell));
                }
                sb.Append(']');
            }
            sb.Append("],\"total\":").Append(totalRows.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"shown\":").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        private static string JsonString(string value) {
            if (value == null) {
                return "null";
            }
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var ch in value) {
                switch (ch) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        } else {
                            sb.Append(ch);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // --- inline CSS (scoped to this grid's id) -----------------------------

        private static string Style(string id) {
            var s = "#" + id;
            return "<style>" +
                s + "{font-family:var(--vscode-font-family,-apple-system,Segoe UI,sans-serif);font-size:var(--vscode-font-size,13px);color:var(--vscode-foreground,#1f1f1f);display:inline-block;max-width:100%;border:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));border-radius:4px;overflow:hidden}" +
                s + " .ck-toolbar{display:flex;align-items:center;gap:8px;padding:6px 8px;border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));background:var(--vscode-editorWidget-background,rgba(128,128,128,.06))}" +
                s + " .ck-filter{flex:1 1 auto;min-width:120px;padding:3px 6px;font:inherit;color:var(--vscode-input-foreground,inherit);background:var(--vscode-input-background,rgba(128,128,128,.12));border:1px solid var(--vscode-input-border,rgba(128,128,128,.35));border-radius:3px;outline:none}" +
                s + " .ck-analyze{font:inherit;cursor:pointer;padding:3px 10px;color:var(--vscode-button-secondaryForeground,inherit);background:var(--vscode-button-secondaryBackground,rgba(128,128,128,.16));border:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));border-radius:3px}" +
                s + " .ck-analyze.ck-on{background:var(--vscode-button-background,#0a64c2);color:var(--vscode-button-foreground,#fff)}" +
                s + " .ck-count{margin-left:auto;opacity:.7;font-size:11px;white-space:nowrap}" +
                s + " .ck-scroll{overflow:auto;max-height:420px}" +
                s + " table{border-collapse:collapse;width:100%}" +
                s + " th,td{text-align:left;padding:3px 10px;border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.18));white-space:nowrap;max-width:360px;overflow:hidden;text-overflow:ellipsis}" +
                s + " thead th{position:sticky;top:0;cursor:pointer;user-select:none;background:var(--vscode-editorWidget-background,rgba(128,128,128,.09));font-weight:600;z-index:1}" +
                s + " thead th:hover{background:var(--vscode-list-hoverBackground,rgba(128,128,128,.18))}" +
                s + " thead th .ck-arrow{opacity:.5;font-size:10px;margin-left:4px}" +
                s + " td.ck-num{text-align:right;font-variant-numeric:tabular-nums}" +
                s + " td.ck-null{opacity:.5;font-style:italic}" +
                s + " tbody tr:hover{background:var(--vscode-list-hoverBackground,rgba(128,128,128,.10))}" +
                s + " .ck-analyze-panel{padding:6px 8px;border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));overflow:auto}" +
                s + " .ck-analyze-panel table{width:auto;min-width:100%}" +
                s + " .ck-analyze-panel td,.ck-analyze-panel th{font-size:11px}" +
                "</style>";
        }

        // --- inline grid script ------------------------------------------------

        private static string Script(string id) {
            // IIFE keyed to this grid's id: reads the embedded JSON, renders the
            // body, and wires sort/filter/analyze. Multiple grids in one output
            // coexist because every selector is rooted at the unique root element.
            return "<script>(function(){" +
                "var root=document.getElementById('" + id + "');" +
                "if(!root||root.dataset.ckInit)return;root.dataset.ckInit='1';" +
                "var d=JSON.parse(root.querySelector('.ck-data').textContent);" +
                "var cols=d.cols,types=d.types,rows=d.rows;" +
                "var sortCol=-1,sortDir=1,filter='';" +
                "var thead=root.querySelector('thead'),tbody=root.querySelector('tbody');" +
                "var countEl=root.querySelector('.ck-count');" +
                "function isNum(t){return t==='number';}" +
                "function num(v){if(v==null)return NaN;var n=parseFloat(String(v).replace(/[$,%\\s]/g,''));return n;}" +
                "function cmp(a,b,t){var an=a==null,bn=b==null;if(an&&bn)return 0;if(an)return 1;if(bn)return -1;" +
                "if(t==='number'){var x=num(a),y=num(b);if(isNaN(x)&&isNaN(y))return 0;if(isNaN(x))return 1;if(isNaN(y))return -1;return x-y;}" +
                "if(t==='date'){var dx=Date.parse(a),dy=Date.parse(b);if(isNaN(dx)&&isNaN(dy))return 0;if(isNaN(dx))return 1;if(isNaN(dy))return -1;return dx-dy;}" +
                "return String(a).toLowerCase()<String(b).toLowerCase()?-1:(String(a).toLowerCase()>String(b).toLowerCase()?1:0);}" +
                "function esc(s){var e=document.createElement('span');e.textContent=s;return e.innerHTML;}" +
                "function header(){var h='<tr>';for(var i=0;i<cols.length;i++){var ar=sortCol===i?(sortDir>0?'▲':'▼'):'';h+='<th data-c=\"'+i+'\">'+esc(cols[i])+'<span class=\"ck-arrow\">'+ar+'</span></th>';}h+='</tr>';thead.innerHTML=h;}" +
                "function visibleRows(){var out=[];var f=filter.trim().toLowerCase();for(var r=0;r<rows.length;r++){if(f){var hit=false;for(var c=0;c<rows[r].length;c++){var v=rows[r][c];if(v!=null&&String(v).toLowerCase().indexOf(f)>=0){hit=true;break;}}if(!hit)continue;}out.push(rows[r]);}" +
                "if(sortCol>=0){var t=types[sortCol];out.sort(function(a,b){return cmp(a[sortCol],b[sortCol],t)*sortDir;});}return out;}" +
                "function body(vis){var h='';for(var r=0;r<vis.length;r++){h+='<tr>';for(var c=0;c<cols.length;c++){var v=vis[r][c];var cls=v==null?'ck-null':(isNum(types[c])?'ck-num':'');h+='<td'+(cls?' class=\"'+cls+'\"':'')+'>'+(v==null?'null':esc(v))+'</td>';}h+='</tr>';}tbody.innerHTML=h;" +
                "var msg=vis.length+' row'+(vis.length===1?'':'s');if(d.total<0)msg+=' · showing first '+d.shown+'+';else if(d.shown<d.total)msg+=' · showing first '+d.shown+' of '+d.total;else if(filter.trim())msg+=' of '+rows.length;countEl.textContent=msg;}" +
                "function stats(){var f=filter.trim().toLowerCase();var vis=visibleRows();var h='<table><thead><tr><th>Column</th><th>Type</th><th>Non-null</th><th>Distinct</th><th>Min</th><th>Max</th><th>Mean</th></tr></thead><tbody>';" +
                "for(var c=0;c<cols.length;c++){var nn=0,seen={},dc=0,mn=null,mx=null,sum=0,ns=0;for(var r=0;r<vis.length;r++){var v=vis[r][c];if(v==null)continue;nn++;if(!seen[v]){seen[v]=1;dc++;}" +
                "if(types[c]==='number'){var x=num(v);if(!isNaN(x)){ns++;sum+=x;if(mn==null||x<mn)mn=x;if(mx==null||x>mx)mx=x;}}" +
                "else{if(mn==null||String(v)<mn)mn=String(v);if(mx==null||String(v)>mx)mx=String(v);}}" +
                "var mean=ns>0?(sum/ns):null;var fmt=function(x){return x==null?'':(typeof x==='number'?(Math.round(x*1000)/1000):esc(x));};" +
                "h+='<tr><td>'+esc(cols[c])+'</td><td>'+types[c]+'</td><td class=\"ck-num\">'+nn+'</td><td class=\"ck-num\">'+dc+'</td><td class=\"ck-num\">'+fmt(mn)+'</td><td class=\"ck-num\">'+fmt(mx)+'</td><td class=\"ck-num\">'+(mean==null?'':fmt(mean))+'</td></tr>';}" +
                "h+='</tbody></table>';return h;}" +
                "var panel=root.querySelector('.ck-analyze-panel'),aBtn=root.querySelector('.ck-analyze');" +
                "function refreshPanel(){if(panel.style.display!=='none')panel.innerHTML=stats();}" +
                "function render(){header();body(visibleRows());refreshPanel();}" +
                "thead.addEventListener('click',function(e){var th=e.target.closest('th');if(!th)return;var c=+th.getAttribute('data-c');if(sortCol===c)sortDir=-sortDir;else{sortCol=c;sortDir=1;}render();});" +
                "root.querySelector('.ck-filter').addEventListener('input',function(e){filter=e.target.value;render();});" +
                "aBtn.addEventListener('click',function(){var on=panel.style.display==='none';panel.style.display=on?'block':'none';aBtn.classList.toggle('ck-on',on);if(on)panel.innerHTML=stats();});" +
                "render();" +
                "})();</script>";
        }

        // --- column-type inference from CLR types ------------------------------

        /// <summary>
        /// Maps a CLR type to the grid's column kind (<see cref="Number"/>,
        /// <see cref="Date"/>, or <see cref="Text"/>). Nullable&lt;T&gt; is
        /// unwrapped. Unknown types fall back to text.
        /// </summary>
        public static string KindOf(Type type) {
            if (type == null) {
                return Text;
            }
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) {
                type = underlying;
            }
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal)) {
                return Number;
            }
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) {
                return Date;
            }
            return Text;
        }

        /// <summary>Formats a cell value to its display string (null stays null).</summary>
        public static string CellText(object value) {
            if (value == null || value is DBNull) {
                return null;
            }
            if (value is string s) {
                return s;
            }
            if (value is IFormattable formattable) {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        /// <summary>Convenience: HTML-encode a string (shared with callers).</summary>
        internal static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
