/* PYRE skin layer — presentation only.
 *
 * Some wording is baked into the frozen logic files (e.g. haze-start.js writes
 * "starting Haze once for this page", and haze-session.js builds a menu containing
 * "HAZE / YOUR TOOLKIT" and "Haze menu"). Those files are deliberately untouched,
 * so this layer rebrands the wording in the DOM after the fact.
 *
 * It cannot change behaviour:
 *   - it only edits text nodes and only ever swaps the brand tokens below;
 *   - it never touches #console or #startup-error, so exploit/kernel output is
 *     reported exactly as the code produced it;
 *   - everything is wrapped in try/catch and the whole layer no-ops on any surprise.
 * Delete the <script> tag in run.html to remove it entirely.
 */
(function () {
  try {
    var TOKENS = [
      ['HAZE', 'PYRE'],
      ['Haze', 'PYRE'],
      ['haze', 'PYRE']
    ];
    var seen = new WeakSet();

    function blocked(node) {
      for (var el = node.parentNode; el && el.nodeType === 1; el = el.parentNode) {
        if (el.id === 'console' || el.id === 'startup-error') return true;
        var tag = el.tagName;
        if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'CANVAS' || tag === 'TEXTAREA') return true;
      }
      return false;
    }

    function rewrite(text) {
      for (var i = 0; i < TOKENS.length; i++) {
        if (text.indexOf(TOKENS[i][0]) !== -1) text = text.split(TOKENS[i][0]).join(TOKENS[i][1]);
      }
      return text;
    }

    function walk(root) {
      if (!root || blocked(root)) return;
      // Text nodes are always re-checked: the scripts replace text by assigning
      // textContent, and a rewrite is idempotent, so there is no loop risk.
      if (root.nodeType === 3) {
        var next = rewrite(root.nodeValue || '');
        if (next !== root.nodeValue) root.nodeValue = next;
        return;
      }
      if (root.nodeType !== 1 && root.nodeType !== 9) return;
      if (seen.has(root)) return;
      seen.add(root);
      var kids = root.childNodes;
      for (var i = 0; i < kids.length; i++) walk(kids[i]);
    }

    var body = document.body || document.documentElement;
    if (!body) return;
    walk(body);

    if (typeof MutationObserver === 'function') {
      new MutationObserver(function (records) {
        try {
          for (var i = 0; i < records.length; i++) {
            var r = records[i];
            if (r.type === 'characterData') walk(r.target);
            else for (var j = 0; j < r.addedNodes.length; j++) walk(r.addedNodes[j]);
          }
        } catch (e) {}
      }).observe(body, { childList: true, subtree: true, characterData: true });
    }
  } catch (e) {}
}());
