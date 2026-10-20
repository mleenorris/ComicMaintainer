        // ComicMaintainer SPA — HTML templating helpers (html``, rawHtml, jsArg, renderHtml, ...).
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // =================================================================
        // HTML templating helpers
        // =================================================================
        //
        // The UI is rendered by building markup strings. Doing that with bare
        // `innerHTML` and hand-placed escapeHtml() calls is error-prone: every
        // new interpolation is an opportunity to forget the escape, and such a
        // mistake is invisible until someone has a quote or an angle bracket in
        // a series title (or a hostile filename).
        //
        // `html` is a tagged template that escapes every interpolated value by
        // default, so the safe thing is what you get for free and unescaped
        // output has to be asked for explicitly via rawHtml()/jsArg(). It
        // returns a marker object rather than a string so that nesting one
        // template inside another does not double-escape the inner markup.
        //
        // Rendering goes through a <template> element, whose parsed content is
        // cached per markup string and cloned on use. Cloning an already-parsed
        // fragment avoids re-running the HTML parser for markup the app has
        // rendered before (loading and empty states, repeated list rows), and
        // it keeps the parse away from the live document: the nodes are only
        // attached once fully built.
        //
        // escapeHtml() itself lives in /js/shared.js, which reader.html loads too.

        function escapeJs(text) {
            // Produce a value safe for use inside a single-quoted JavaScript string
            // that is itself embedded in a double-quoted HTML attribute (e.g.
            // onclick="doStuff('${escapeJs(value)}')"). Backslashes, single quotes,
            // and control characters are JS-escaped. The double-quote character must
            // be HTML-entity-encoded — not JS-escaped — because the browser parses
            // the attribute value before handing it to the JS parser, so a literal
            // " would otherwise terminate the attribute and break the markup
            // (e.g. for series titles like: Hazure Zokusei "Hikari Mahou" ga ...).
            // After HTML decoding the JS sees a plain " inside a single-quoted
            // string, which is valid.
            //
            // & must be encoded to &amp; first (before " is turned into &quot;),
            // otherwise a value containing a literal HTML entity such as
            // "&#39;" would survive JS-escaping unchanged and then get
            // HTML-decoded back into a raw ' by the browser, terminating the
            // single-quoted JS string early and allowing injected code to run.
            return text.replace(/&/g, '&amp;')
                       .replace(/\\/g, '\\\\')
                       .replace(/'/g, "\\'")
                       .replace(/"/g, '&quot;')
                       .replace(/\n/g, '\\n')
                       .replace(/\r/g, '\\r')
                       .replace(/\t/g, '\\t');
        }

        const TRUSTED_HTML = Symbol('trustedHtml');

        /**
         * Mark a string as already-safe markup that must be inlined verbatim.
         * Only ever pass markup this code generated - never raw server or user
         * data.
         */
        function rawHtml(value) {
            return { [TRUSTED_HTML]: true, value: String(value ?? '') };
        }

        /**
         * Markup for an icon from the inline sprite in index.html. Usable both
         * inside html`` (it is trusted markup) and in plain template strings
         * (it stringifies to the same markup). `name` must be a sprite id
         * literal from this file, never user data.
         */
        function uiIcon(name) {
            const markup = `<svg class="icon" aria-hidden="true"><use href="#i-${name}"/></svg>`;
            return { ...rawHtml(markup), toString: () => markup };
        }

        function isTrustedHtml(value) {
            return !!value && typeof value === 'object' && value[TRUSTED_HTML] === true;
        }

        /**
         * Escape a value for use as an argument of an inline event handler,
         * e.g. html`<button onclick="doThing('${jsArg(path)}')">`.
         *
         * This deliberately bypasses HTML escaping: the browser HTML-decodes an
         * attribute value *before* the JS parser sees it, so HTML-escaping a
         * quote here would decode straight back into a string-terminating
         * quote. escapeJs() produces the one encoding that survives both
         * parsers - see its own comment for the details.
         */
        function jsArg(value) {
            return rawHtml(escapeJs(String(value ?? '')));
        }

        /**
         * Convert a single interpolated value to markup.
         *  - trusted values (rawHtml/jsArg/nested html) are inlined as-is
         *  - null/undefined/false are dropped, so `cond && html`...`` works
         *  - arrays are flattened, so `items.map(...)` needs no .join('')
         *  - everything else is stringified and HTML-escaped
         */
        function htmlValueToString(value) {
            if (value === null || value === undefined || value === false || value === true) {
                return '';
            }
            if (isTrustedHtml(value)) {
                return value.value;
            }
            if (Array.isArray(value)) {
                return value.map(htmlValueToString).join('');
            }
            return escapeHtml(String(value));
        }

        /** Tagged template producing auto-escaped, trusted markup. */
        function html(strings, ...values) {
            let out = strings[0];
            for (let i = 0; i < values.length; i++) {
                out += htmlValueToString(values[i]) + strings[i + 1];
            }
            return rawHtml(out);
        }

        /** Unwrap an html`` result (or plain value) to a markup string. */
        function htmlToString(content) {
            return htmlValueToString(content);
        }

        // Parsed <template> elements keyed by markup string. Bounded because
        // interpolated markup produces a distinct key per distinct data, and an
        // unbounded map here would retain every row the app ever rendered.
        const TEMPLATE_CACHE = new Map();
        const MAX_TEMPLATE_CACHE_ENTRIES = 200;

        function getParsedTemplate(markup) {
            const cached = TEMPLATE_CACHE.get(markup);
            if (cached) {
                // Re-insert so the map's insertion order doubles as recency
                // order, making the eviction below least-recently-used.
                TEMPLATE_CACHE.delete(markup);
                TEMPLATE_CACHE.set(markup, cached);
                return cached;
            }

            const template = document.createElement('template');
            template.innerHTML = markup;
            if (TEMPLATE_CACHE.size >= MAX_TEMPLATE_CACHE_ENTRIES) {
                TEMPLATE_CACHE.delete(TEMPLATE_CACHE.keys().next().value);
            }
            TEMPLATE_CACHE.set(markup, template);
            return template;
        }

        /** Build a DocumentFragment by cloning the parsed template content. */
        function htmlToFragment(content) {
            return getParsedTemplate(htmlToString(content)).content.cloneNode(true);
        }

        /** Build the first element of a template, or null if there is none. */
        function htmlToElement(content) {
            return htmlToFragment(content).firstElementChild;
        }

        /**
         * Replace `target`'s children with freshly cloned template content.
         * Use instead of `target.innerHTML = ...`.
         */
        function renderHtml(target, content) {
            if (!target) return target;
            target.replaceChildren(htmlToFragment(content));
            return target;
        }

        /**
         * Append cloned template content to `target` without disturbing the
         * children it already has.
         */
        function appendHtml(target, content) {
            if (!target) return target;
            target.appendChild(htmlToFragment(content));
            return target;
        }

        /** Remove every child of `target`. Use instead of `innerHTML = ''`. */
        function clearChildren(target) {
            if (!target) return target;
            target.replaceChildren();
            return target;
        }
