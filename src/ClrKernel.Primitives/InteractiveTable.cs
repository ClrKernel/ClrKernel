using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace ClrKernel.Primitives {
    /// <summary>
    /// Builds a self-contained interactive HTML grid for tabular data — the kind of
    /// output <c>DisplayTable()</c> produces. Features: click-to-sort headers, a global
    /// row filter, a per-column filter row (type to filter each column), a per-column
    /// dropdown of distinct values (search + checkboxes), and an Analyze panel of
    /// per-column stats. All filters combine (AND). Everything (markup, CSS, JS, and the
    /// row data as embedded JSON) is emitted inline so it renders in a VS Code notebook
    /// <c>text/html</c> output, JupyterLab, or a plain browser with no external assets.
    /// Colors come from VS Code theme variables with fallbacks, so it blends into light
    /// and dark themes.
    /// </summary>
    public static class InteractiveTable {
        /// <summary>Column kinds the grid understands for sorting and stats.</summary>
        public const string Number = "number";
        public const string Date = "date";
        public const string Text = "string";

        /// <summary>
        /// Renders the grid. <paramref name="columns"/> are header labels;
        /// <paramref name="rows"/> holds already-stringified cell values (one inner list
        /// per row, aligned to <paramref name="columns"/>); <paramref name="types"/> gives
        /// each column's kind (<see cref="Number"/>, <see cref="Date"/>, or
        /// <see cref="Text"/>) for type-aware sorting and stats. <paramref name="totalRows"/>
        /// is the full row count before any limit so a truncated grid can say
        /// "showing first N of M".
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
            sb.Append("<div class=\"ck-inner\">");
            sb.Append("<div class=\"ck-toolbar\">")
                .Append("<input class=\"ck-filter\" type=\"text\" placeholder=\"Filter all columns…\" />")
                .Append("<button class=\"ck-clear\" type=\"button\" title=\"Clear all filters\">Clear</button>")
                .Append("<button class=\"ck-analyze\" type=\"button\">Analyze</button>")
                .Append("<span class=\"ck-count\"></span>")
                .Append("</div>");
            sb.Append("<div class=\"ck-analyze-panel\" style=\"display:none\"></div>");
            sb.Append("<div class=\"ck-scroll\"><table><thead></thead><tbody></tbody></table></div>");
            sb.Append("</div>"); // .ck-inner
            sb.Append("<div class=\"ck-pop\" style=\"display:none\"></div>");

            // Data payload — parsed by the grid script. Escaping "</" keeps the browser
            // from ending the enclosing element early; it stays valid JSON.
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
                s + "{position:relative;display:inline-block;max-width:100%;font-family:var(--vscode-font-family,-apple-system,Segoe UI,sans-serif);font-size:var(--vscode-font-size,13px);color:var(--vscode-foreground,#1f1f1f)}" +
                s + " .ck-inner{border:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));border-radius:4px;overflow:hidden}" +
                s + " .ck-toolbar{display:flex;align-items:center;gap:8px;padding:6px 8px;border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));background:var(--vscode-editorWidget-background,rgba(128,128,128,.06))}" +
                s + " .ck-filter{flex:1 1 auto;min-width:120px;padding:3px 6px;font:inherit;color:var(--vscode-input-foreground,inherit);background:var(--vscode-input-background,rgba(128,128,128,.12));border:1px solid var(--vscode-input-border,rgba(128,128,128,.35));border-radius:3px;outline:none}" +
                s + " .ck-clear," + s + " .ck-analyze{font:inherit;cursor:pointer;padding:3px 10px;color:var(--vscode-button-secondaryForeground,inherit);background:var(--vscode-button-secondaryBackground,rgba(128,128,128,.16));border:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));border-radius:3px}" +
                s + " .ck-analyze.ck-on{background:var(--vscode-button-background,#0a64c2);color:var(--vscode-button-foreground,#fff)}" +
                s + " .ck-count{margin-left:auto;opacity:.7;font-size:11px;white-space:nowrap}" +
                s + " .ck-scroll{overflow:auto;max-height:420px}" +
                s + " table{border-collapse:collapse;width:100%}" +
                s + " th,td{text-align:left;padding:3px 10px;border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.18));white-space:nowrap;max-width:360px;overflow:hidden;text-overflow:ellipsis}" +
                s + " thead .ck-h th{position:sticky;top:0;cursor:pointer;user-select:none;background:var(--vscode-editorWidget-background,rgba(128,128,128,.09));font-weight:600;z-index:2}" +
                s + " thead .ck-h th:hover{background:var(--vscode-list-hoverBackground,rgba(128,128,128,.18))}" +
                s + " thead .ck-arrow{opacity:.5;font-size:10px;margin-left:4px}" +
                s + " thead .ck-fbtn{opacity:.35;font-size:10px;margin-left:6px;padding:0 3px;border-radius:3px;cursor:pointer}" +
                s + " thead .ck-fbtn:hover{opacity:.9;background:var(--vscode-toolbar-hoverBackground,rgba(128,128,128,.25))}" +
                s + " thead .ck-fbtn.ck-active{opacity:1;color:var(--vscode-button-background,#0a64c2)}" +
                s + " thead .ck-f th{position:sticky;top:26px;background:var(--vscode-editorWidget-background,rgba(128,128,128,.05));padding:2px 4px;z-index:1}" +
                s + " .ck-cfilter{width:100%;box-sizing:border-box;padding:2px 5px;font:inherit;font-size:11px;color:var(--vscode-input-foreground,inherit);background:var(--vscode-input-background,rgba(128,128,128,.10));border:1px solid var(--vscode-input-border,rgba(128,128,128,.3));border-radius:3px;outline:none}" +
                s + " td.ck-num{text-align:right;font-variant-numeric:tabular-nums}" +
                s + " td.ck-null{opacity:.5;font-style:italic}" +
                s + " tbody tr:hover{background:var(--vscode-list-hoverBackground,rgba(128,128,128,.10))}" +
                s + " .ck-analyze-panel{padding:6px 8px;border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));overflow:auto}" +
                s + " .ck-analyze-panel table{width:auto;min-width:100%}" +
                s + " .ck-analyze-panel td,.ck-analyze-panel th{font-size:11px}" +
                s + " .ck-pop{position:absolute;z-index:50;min-width:180px;max-width:280px;background:var(--vscode-editorWidget-background,var(--vscode-editor-background,#fff));border:1px solid var(--vscode-panel-border,rgba(128,128,128,.5));border-radius:4px;box-shadow:0 2px 8px rgba(0,0,0,.25);padding:6px}" +
                s + " .ck-pop-search input{width:100%;box-sizing:border-box;padding:3px 6px;font:inherit;font-size:12px;color:var(--vscode-input-foreground,inherit);background:var(--vscode-input-background,rgba(128,128,128,.12));border:1px solid var(--vscode-input-border,rgba(128,128,128,.35));border-radius:3px;outline:none}" +
                s + " .ck-pop-actions{font-size:11px;padding:4px 2px}" +
                s + " .ck-pop-actions a{color:var(--vscode-textLink-foreground,#3794ff);text-decoration:none;cursor:pointer}" +
                s + " .ck-pop-list{max-height:200px;overflow:auto;margin-top:2px}" +
                s + " .ck-pop-item{display:flex;align-items:center;gap:6px;padding:2px 3px;font-size:12px;white-space:nowrap;cursor:pointer}" +
                s + " .ck-pop-item:hover{background:var(--vscode-list-hoverBackground,rgba(128,128,128,.15))}" +
                s + " .ck-pop-item span{overflow:hidden;text-overflow:ellipsis}" +
                "</style>";
        }

        // --- inline grid script ------------------------------------------------

        private static string Script(string id) {
            // The id is injected as a JS string so the body can be a plain verbatim
            // block. IIFE keyed to this grid's root so multiple grids coexist.
            return "<script>(function(){var CKID=" + JsonString(id) + ";" + _gridJs + "})();</script>";
        }

        private const string _gridJs = @"
var root=document.getElementById(CKID);
if(!root||root.dataset.ckInit)return;root.dataset.ckInit='1';
var d=JSON.parse(root.querySelector('.ck-data').textContent);
var cols=d.cols,types=d.types,rows=d.rows;
var sortCol=-1,sortDir=1,gfilter='';
var colText=cols.map(function(){return '';});
var colSel=cols.map(function(){return null;});   // null = all values allowed
var distinct=cols.map(function(){return null;});  // lazy per-column distinct list
var NULLK=' null';
var thead=root.querySelector('thead'),tbody=root.querySelector('tbody'),countEl=root.querySelector('.ck-count');
thead.innerHTML='<tr class=""ck-h""></tr><tr class=""ck-f""></tr>';
var hRow=thead.querySelector('.ck-h'),fRow=thead.querySelector('.ck-f');
var pop=root.querySelector('.ck-pop'),popCol=-1;
function isNum(t){return t==='number';}
function num(v){if(v==null)return NaN;return parseFloat(String(v).replace(/[$,%\s]/g,''));}
function cmp(a,b,t){var an=a==null,bn=b==null;if(an&&bn)return 0;if(an)return 1;if(bn)return -1;
if(t==='number'){var x=num(a),y=num(b);if(isNaN(x)&&isNaN(y))return 0;if(isNaN(x))return 1;if(isNaN(y))return -1;return x-y;}
if(t==='date'){var dx=Date.parse(a),dy=Date.parse(b);if(isNaN(dx)&&isNaN(dy))return 0;if(isNaN(dx))return 1;if(isNaN(dy))return -1;return dx-dy;}
return String(a).toLowerCase()<String(b).toLowerCase()?-1:(String(a).toLowerCase()>String(b).toLowerCase()?1:0);}
function esc(s){var e=document.createElement('span');e.textContent=s==null?'':s;return e.innerHTML;}
function escA(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/""/g,'&quot;');}
function keyOf(v){return v==null?NULLK:String(v);}
function funnelActive(c){return !!colText[c]||!!colSel[c];}
function passRow(row){
if(gfilter){var hit=false;for(var c=0;c<row.length;c++){var v=row[c];if(v!=null&&String(v).toLowerCase().indexOf(gfilter)>=0){hit=true;break;}}if(!hit)return false;}
for(var i=0;i<cols.length;i++){var v=row[i];
if(colText[i]){var s=v==null?'':String(v).toLowerCase();if(s.indexOf(colText[i])<0)return false;}
if(colSel[i]&&!colSel[i][keyOf(v)])return false;}
return true;}
function visibleRows(){var out=[];for(var r=0;r<rows.length;r++){if(passRow(rows[r]))out.push(rows[r]);}
if(sortCol>=0){var t=types[sortCol];out.sort(function(a,b){return cmp(a[sortCol],b[sortCol],t)*sortDir;});}return out;}
function buildHeader(){var h='';for(var i=0;i<cols.length;i++){var ar=sortCol===i?(sortDir>0?'▲':'▼'):'';
h+='<th data-c=""'+i+'""><span class=""ck-lbl"">'+esc(cols[i])+'</span><span class=""ck-arrow"">'+ar+'</span><span class=""ck-fbtn'+(funnelActive(i)?' ck-active':'')+'"" data-f=""'+i+'"" title=""Filter this column"">▾</span></th>';}
hRow.innerHTML=h;}
function buildFilterRow(){var h='';for(var i=0;i<cols.length;i++){h+='<th><input class=""ck-cfilter"" type=""text"" data-c=""'+i+'"" placeholder=""filter"" value=""'+escA(colText[i])+'"" /></th>';}fRow.innerHTML=h;}
function updateFunnels(){var fs=hRow.querySelectorAll('.ck-fbtn');for(var i=0;i<fs.length;i++){var c=+fs[i].getAttribute('data-f');fs[i].classList.toggle('ck-active',funnelActive(c));}}
function body(){var vis=visibleRows();var h='';for(var r=0;r<vis.length;r++){h+='<tr>';for(var c=0;c<cols.length;c++){var v=vis[r][c];var cls=v==null?'ck-null':(isNum(types[c])?'ck-num':'');h+='<td'+(cls?' class=""'+cls+'""':'')+'>'+(v==null?'null':esc(v))+'</td>';}h+='</tr>';}tbody.innerHTML=h;
var msg=vis.length+' row'+(vis.length===1?'':'s');if(d.total<0)msg+=' · first '+d.shown+'+';else if(d.shown<d.total)msg+=' · first '+d.shown+' of '+d.total;else if(vis.length<rows.length)msg+=' of '+rows.length;countEl.textContent=msg;refreshPanel();}
function stats(){var vis=visibleRows();var h='<table><thead><tr><th>Column</th><th>Type</th><th>Non-null</th><th>Distinct</th><th>Min</th><th>Max</th><th>Mean</th></tr></thead><tbody>';
for(var c=0;c<cols.length;c++){var nn=0,seen={},dc=0,mn=null,mx=null,sum=0,ns=0;for(var r=0;r<vis.length;r++){var v=vis[r][c];if(v==null)continue;nn++;if(!seen[v]){seen[v]=1;dc++;}
if(types[c]==='number'){var x=num(v);if(!isNaN(x)){ns++;sum+=x;if(mn==null||x<mn)mn=x;if(mx==null||x>mx)mx=x;}}
else{if(mn==null||String(v)<mn)mn=String(v);if(mx==null||String(v)>mx)mx=String(v);}}
var mean=ns>0?(sum/ns):null;var fmt=function(x){return x==null?'':(typeof x==='number'?(Math.round(x*1000)/1000):esc(x));};
h+='<tr><td>'+esc(cols[c])+'</td><td>'+types[c]+'</td><td class=""ck-num"">'+nn+'</td><td class=""ck-num"">'+dc+'</td><td class=""ck-num"">'+fmt(mn)+'</td><td class=""ck-num"">'+fmt(mx)+'</td><td class=""ck-num"">'+(mean==null?'':fmt(mean))+'</td></tr>';}
h+='</tbody></table>';return h;}
var panel=root.querySelector('.ck-analyze-panel'),aBtn=root.querySelector('.ck-analyze');
function refreshPanel(){if(panel.style.display!=='none')panel.innerHTML=stats();}
function getDistinct(c){if(distinct[c])return distinct[c];var seen={},list=[];for(var r=0;r<rows.length;r++){var k=keyOf(rows[r][c]);if(!seen[k]){seen[k]=1;list.push(k);}}
list.sort(function(a,b){if(a===NULLK)return -1;if(b===NULLK)return 1;return cmp(a,b,types[c]);});distinct[c]=list;return list;}
function applyPop(){var boxes=pop.querySelectorAll('.ck-pop-list input');var all=true,map={};for(var i=0;i<boxes.length;i++){var k=boxes[i].getAttribute('data-k');if(boxes[i].checked)map[k]=true;else all=false;}colSel[popCol]=all?null:map;body();updateFunnels();}
function openPop(c,anchor){popCol=c;var list=getDistinct(c),sel=colSel[c];
var h='<div class=""ck-pop-search""><input type=""text"" placeholder=""Search values…"" /></div><div class=""ck-pop-actions""><a class=""ck-all"">Select all</a> · <a class=""ck-none"">Clear</a></div><div class=""ck-pop-list"">';
for(var i=0;i<list.length;i++){var k=list[i];var ck=(sel==null)||!!sel[k];var lbl=k===NULLK?'(null)':k;h+='<label class=""ck-pop-item""><input type=""checkbox"" data-k=""'+escA(k)+'"" '+(ck?'checked':'')+' /><span>'+esc(lbl)+'</span></label>';}
h+='</div>';pop.innerHTML=h;
var rr=root.getBoundingClientRect(),ar=anchor.getBoundingClientRect();
pop.style.left=Math.max(0,ar.left-rr.left)+'px';pop.style.top=(ar.bottom-rr.top+2)+'px';pop.style.display='block';
var si=pop.querySelector('.ck-pop-search input');
si.addEventListener('input',function(){var q=this.value.toLowerCase();var items=pop.querySelectorAll('.ck-pop-item');for(var j=0;j<items.length;j++){items[j].style.display=items[j].textContent.toLowerCase().indexOf(q)>=0?'':'none';}});
si.focus();}
function closePop(){pop.style.display='none';popCol=-1;}
pop.addEventListener('change',function(e){if(e.target&&e.target.type==='checkbox')applyPop();});
pop.addEventListener('click',function(e){var t=e.target;if(t.classList.contains('ck-all')||t.classList.contains('ck-none')){var check=t.classList.contains('ck-all');var b=pop.querySelectorAll('.ck-pop-list input');for(var i=0;i<b.length;i++)b[i].checked=check;applyPop();}});
hRow.addEventListener('click',function(e){var fb=e.target.closest?e.target.closest('.ck-fbtn'):null;if(fb){e.stopPropagation();var fc=+fb.getAttribute('data-f');if(popCol===fc){closePop();}else{openPop(fc,fb);}return;}
var th=e.target.closest?e.target.closest('th'):null;if(!th)return;var c=+th.getAttribute('data-c');if(sortCol===c)sortDir=-sortDir;else{sortCol=c;sortDir=1;}buildHeader();body();});
fRow.addEventListener('input',function(e){var inp=e.target;if(!inp.classList||!inp.classList.contains('ck-cfilter'))return;var c=+inp.getAttribute('data-c');colText[c]=inp.value.trim().toLowerCase();body();updateFunnels();});
root.querySelector('.ck-filter').addEventListener('input',function(e){gfilter=e.target.value.trim().toLowerCase();body();});
root.querySelector('.ck-clear').addEventListener('click',function(){gfilter='';for(var c=0;c<cols.length;c++){colText[c]='';colSel[c]=null;}root.querySelector('.ck-filter').value='';buildFilterRow();closePop();buildHeader();body();});
aBtn.addEventListener('click',function(){var on=panel.style.display==='none';panel.style.display=on?'block':'none';aBtn.classList.toggle('ck-on',on);if(on)panel.innerHTML=stats();});
document.addEventListener('click',function(e){if(pop.style.display==='none')return;if(pop.contains(e.target))return;if(e.target.classList&&e.target.classList.contains('ck-fbtn'))return;closePop();});
buildHeader();buildFilterRow();body();
";

        // --- column-type inference from CLR types ------------------------------

        /// <summary>
        /// Maps a CLR type to the grid's column kind (<see cref="Number"/>,
        /// <see cref="Date"/>, or <see cref="Text"/>). Nullable&lt;T&gt; is unwrapped.
        /// Unknown types fall back to text.
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
