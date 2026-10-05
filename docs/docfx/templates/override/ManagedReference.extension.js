// Override of the default template's (no-op) ManagedReference extension.
// DocFX merges template folders by relative path and the primary script does
// require('./ManagedReference.extension.js'), so this file replaces the stock
// one because "templates/override" is listed last in docfx.json.
//
// Goal: don't render a Parameters or Returns section that only repeats the
// signature. DocFX synthesizes those sections for every member, even when the
// source has no <param>/<returns> text (e.g. Rules.Alias(Rule inner) shows
// "Parameters: inner Rule" and "Returns: Rule", both already in the signature).
// We blank out the model so the template's existing {{#syntax.parameters.0}}
// and {{#syntax.return}} conditionals skip the section. A member that does document
// its parameters/return (EndOfLine's eofIsEol, Identifier's extraStartRunes,
// WithError's errorMessage/forced, ...) is left untouched.

function isBlank(html) {
  if (html === undefined || html === null) {
    return true;
  }
  // Descriptions arrive as rendered HTML; strip tags before checking for text.
  return String(html).replace(/<[^>]*>/g, '').trim() === '';
}

function pruneSyntax(syntax) {
  if (!syntax) {
    return;
  }
  if (Array.isArray(syntax.parameters)) {
    var anyDocumented = syntax.parameters.some(function (parameter) {
      return parameter && !isBlank(parameter.description);
    });
    // Show the full table when at least one parameter is documented; otherwise
    // drop them all so only the signature carries the names and types.
    if (!anyDocumented) {
      syntax.parameters = [];
    }
  }
  // Returns, Property Value, Field Value, and Event Type each render as a
  // one-row section showing only the type when undocumented (e.g. "Property
  // Value: bool"), which the signature already states. Drop the empty ones.
  ["return", "propertyValue", "fieldValue", "eventType"].forEach(function (key) {
    if (syntax[key] && isBlank(syntax[key].description)) {
      syntax[key] = null;
    }
  });
}

// Ancestors that every type has implicitly. A type whose whole inheritance
// chain is just these didn't really derive from anything, so the "Inheritance"
// line (e.g. "object ← Rules") and its inherited object/ValueType members
// (ToString, Equals, GetHashCode, ...) are noise.
var TRIVIAL_BASES = {
  "System.Object": true,
  "System.ValueType": true,
  "System.Enum": true
};

function pruneInheritance(node) {
  if (!Array.isArray(node.inheritance)) {
    return;
  }
  var derivesFromSomething = node.inheritance.some(function (base) {
    return base && base.uid && !TRIVIAL_BASES[base.uid];
  });
  // Keep the chain and inherited members for a real base (Rule, Exception, ...);
  // drop both when the type only sits on the implicit framework bases.
  if (!derivesFromSomething) {
    node.inheritance = [];
    node.inheritedMembers = [];
  }
}

function escapeHtml(value) {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Collect DocFX's resolved type references, including nested generic arguments.
// Do this before pruning undocumented parameter/return sections.
function declarationLinks(node, declaration) {
  var links = {};
  function collect(value) {
    if (!value || typeof value !== 'object') return;
    if (value.specName && value.specName[0]) {
      var specNameText = value.specName[0].value;
      specNameText.replace(/<xref uid="([^"]+)" text="([^"]+)"\s*\/>/g, function (match, uid, name) {
        links[name] = match;
        return match;
      });
      specNameText.replace(/<a\b[^>]*>([^<]+)<\/a>/g, function (match, name) {
        links[name] = match;
        return match;
      });
    }
    Object.keys(value).forEach(function (key) {
      if (key !== 'children') collect(value[key]);
    });
  }
  collect(node.syntax);
  collect(node.inheritance);
  collect(node.implements);
  var angleDepth = 0;
  // Strings and character literals are kept whole so defaults remain literal.
  return declaration.replace(/@?"(?:""|\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[A-Za-z_][A-Za-z_0-9.]*|[\s\S]/g,
    function (token, offset) {
      if (token === '<') angleDepth++;
      if (token === '>') angleDepth--;
      var rest = declaration.slice(offset + token.length);
      // Outside generics, these suffixes identify member/parameter names,
      // rather than the type preceding them.
      var isName = angleDepth === 0 && /^\s*(?:\(|\{|=|,|\))/.test(rest);
      return links[token] && !isName ? links[token] : escapeHtml(token);
    });
}

function walk(node) {
  if (!node || typeof node !== "object") {
    return;
  }
  // Mustache falls back to parent contexts when a property is absent.
  // Explicitly clear this so no member inherits its containing type's heading.
  node.declarationHeading = '';
  if (node.syntax) {
    // Show declarations once, in the heading. Preserve names and IDs for page
    // titles, inheritance lists, navigation, and links.
    var memberType = String(node.type || "").toLowerCase();
    if (["method", "constructor", "operator", "property", "field", "event", "class", "struct",
         "interface", "enum", "delegate"].indexOf(memberType) !== -1) {
      if (node.syntax.content && node.syntax.content.length) {
        node.declarationHeading = declarationLinks(node, node.syntax.content[0].value);
        node.syntax.content = [];
      }
    }
    pruneSyntax(node.syntax);
  }
  pruneInheritance(node);
  if (Array.isArray(node.children)) {
    node.children.forEach(walk);
  }
}

exports.preTransform = function (model) {
  return model;
}

exports.postTransform = function (model) {
  walk(model);
  return model;
}
