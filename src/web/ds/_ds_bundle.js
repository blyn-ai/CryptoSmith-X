/* @ds-bundle: {"format":4,"namespace":"CryptoSmithXDesignSystem_d88f99","components":[{"name":"Button","sourcePath":"components/core/Button.jsx"},{"name":"Card","sourcePath":"components/core/Card.jsx"},{"name":"KpiTile","sourcePath":"components/core/KpiTile.jsx"},{"name":"SideBadge","sourcePath":"components/core/SideBadge.jsx"},{"name":"Tabs","sourcePath":"components/core/Tabs.jsx"},{"name":"Tag","sourcePath":"components/core/Tag.jsx"},{"name":"Dialog","sourcePath":"components/feedback/Dialog.jsx"},{"name":"Toast","sourcePath":"components/feedback/Toast.jsx"},{"name":"Checkbox","sourcePath":"components/forms/Checkbox.jsx"},{"name":"Input","sourcePath":"components/forms/Input.jsx"},{"name":"Select","sourcePath":"components/forms/Select.jsx"},{"name":"Switch","sourcePath":"components/forms/Switch.jsx"},{"name":"TopNav","sourcePath":"components/navigation/TopNav.jsx"},{"name":"Wordmark","sourcePath":"components/navigation/Wordmark.jsx"},{"name":"EquityCurve","sourcePath":"components/trading/EquityCurve.jsx"},{"name":"PositionsTable","sourcePath":"components/trading/PositionsTable.jsx"},{"name":"StrategyCard","sourcePath":"components/trading/StrategyCard.jsx"},{"name":"VenueStatus","sourcePath":"components/trading/VenueStatus.jsx"}],"sourceHashes":{"components/core/Button.jsx":"f8ae32ddc0c9","components/core/Card.jsx":"363ea4faff3a","components/core/KpiTile.jsx":"683a0f71fccc","components/core/SideBadge.jsx":"5a85096ece11","components/core/Tabs.jsx":"49bfe6370038","components/core/Tag.jsx":"6d44d959c11d","components/feedback/Dialog.jsx":"7f0f35b55b14","components/feedback/Toast.jsx":"82c409f0f179","components/forms/Checkbox.jsx":"ed362d6633fc","components/forms/Input.jsx":"d3314370021a","components/forms/Select.jsx":"d951d5bb7f8b","components/forms/Switch.jsx":"db492f575f87","components/navigation/TopNav.jsx":"c7ada6085e50","components/navigation/Wordmark.jsx":"8699116c6131","components/trading/EquityCurve.jsx":"71eaa060007a","components/trading/PositionsTable.jsx":"b5db268fdd19","components/trading/StrategyCard.jsx":"78e268be7a9d","components/trading/VenueStatus.jsx":"1b98ce72b5e1","ui_kits/console/screens-dashboard.jsx":"2eff87e713f6","ui_kits/console/screens-login.jsx":"549af40d145b","ui_kits/console/screens-settings.jsx":"7db6d31380cb","ui_kits/console/screens-strategies.jsx":"4d0bf2b37c11"},"inlinedExternals":[],"unexposedExports":[]} */

(() => {

const __ds_ns = (window.CryptoSmithXDesignSystem_d88f99 = window.CryptoSmithXDesignSystem_d88f99 || {});

const __ds_scope = {};

(__ds_ns.__errors = __ds_ns.__errors || []);

// components/core/Button.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
const S = {
  base: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
    font: 'var(--type-button)',
    borderRadius: 'var(--radius-sm)',
    border: '1px solid transparent',
    cursor: 'pointer',
    transition: 'var(--transition-color)',
    textDecoration: 'none'
  },
  size: {
    md: {
      padding: '11px 22px'
    },
    sm: {
      padding: '7px 14px',
      fontSize: 12
    },
    lg: {
      padding: '13px 26px',
      fontSize: 14
    }
  },
  variant: {
    primary: {
      background: 'var(--action-primary)',
      color: 'var(--text-on-action)'
    },
    ghost: {
      background: 'none',
      borderColor: 'var(--border-strong)',
      color: 'var(--lilac-200)'
    },
    gold: {
      background: 'var(--gold-400)',
      color: 'var(--text-on-gold)'
    },
    danger: {
      background: 'var(--tint-down)',
      color: 'var(--down-300)',
      borderColor: 'rgba(239,93,111,.35)'
    },
    quiet: {
      background: 'var(--tint-violet)',
      color: 'var(--violet-200)'
    }
  }
};
function Button({
  variant = 'primary',
  size = 'md',
  disabled,
  style,
  children,
  ...rest
}) {
  const [hover, setHover] = React.useState(false);
  const hoverStyle = hover && !disabled ? {
    primary: {
      background: 'var(--action-primary-hover)'
    },
    ghost: {
      borderColor: 'var(--violet-400)',
      color: 'var(--lilac-100)'
    },
    gold: {
      background: 'var(--gold-300)'
    },
    danger: {
      background: 'rgba(239,93,111,.2)'
    },
    quiet: {
      background: 'rgba(161,138,255,.2)'
    }
  }[variant] : null;
  return /*#__PURE__*/React.createElement("button", _extends({}, rest, {
    disabled: disabled,
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => setHover(false),
    style: {
      ...S.base,
      ...S.size[size],
      ...S.variant[variant],
      ...hoverStyle,
      ...(disabled ? {
        opacity: .45,
        cursor: 'not-allowed'
      } : null),
      ...style
    }
  }), children);
}
Object.assign(__ds_scope, { Button });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Button.jsx", error: String((e && e.message) || e) }); }

// components/core/Card.jsx
try { (() => {
function Card({
  title,
  actions,
  pad = true,
  style,
  children
}) {
  return /*#__PURE__*/React.createElement("section", {
    style: {
      background: 'var(--surface-card)',
      border: '1px solid var(--border-hairline)',
      borderRadius: 'var(--radius-md)',
      ...style
    }
  }, (title || actions) && /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      padding: '16px 20px',
      borderBottom: '1px solid var(--border-hairline)'
    }
  }, title && /*#__PURE__*/React.createElement("h2", {
    style: {
      margin: 0,
      font: 'var(--type-card-title)',
      fontSize: 16,
      color: 'var(--text-heading)'
    }
  }, title), actions && /*#__PURE__*/React.createElement("div", {
    style: {
      marginLeft: 'auto',
      display: 'flex',
      gap: 8
    }
  }, actions)), /*#__PURE__*/React.createElement("div", {
    style: pad ? {
      padding: '16px 20px'
    } : null
  }, children));
}
Object.assign(__ds_scope, { Card });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Card.jsx", error: String((e && e.message) || e) }); }

// components/core/KpiTile.jsx
try { (() => {
function KpiTile({
  label,
  value,
  delta,
  deltaTone = 'muted',
  style
}) {
  const toneColor = {
    up: 'var(--pnl-up)',
    down: 'var(--pnl-down)',
    gold: 'var(--accent-gold)',
    muted: 'var(--text-muted)'
  }[deltaTone];
  return /*#__PURE__*/React.createElement("div", {
    style: {
      padding: '16px 18px',
      background: 'var(--surface-card)',
      border: '1px solid var(--border-hairline)',
      borderRadius: 'var(--radius-md)',
      ...style
    }
  }, /*#__PURE__*/React.createElement("u", {
    style: {
      display: 'block',
      textDecoration: 'none',
      font: 'var(--type-eyebrow)',
      letterSpacing: 'var(--track-eyebrow)',
      textTransform: 'uppercase',
      color: 'var(--text-muted)',
      marginBottom: 8
    }
  }, label), /*#__PURE__*/React.createElement("b", {
    style: {
      display: 'block',
      font: 'var(--type-stat)',
      letterSpacing: 'var(--track-stat)',
      color: 'var(--text-heading)'
    }
  }, value), delta != null && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'block',
      font: '500 12px var(--font-mono)',
      marginTop: 6,
      color: toneColor
    }
  }, delta));
}
Object.assign(__ds_scope, { KpiTile });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/KpiTile.jsx", error: String((e && e.message) || e) }); }

// components/core/SideBadge.jsx
try { (() => {
function SideBadge({
  side = 'long',
  style
}) {
  const long = String(side).toLowerCase() === 'long';
  return /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'inline-block',
      font: 'var(--type-badge)',
      fontSize: 10,
      letterSpacing: '.1em',
      padding: '3px 8px',
      borderRadius: 'var(--radius-xs)',
      background: long ? 'var(--tint-up)' : 'var(--tint-down)',
      color: long ? 'var(--long)' : 'var(--short)',
      ...style
    }
  }, long ? 'LONG' : 'SHORT');
}
Object.assign(__ds_scope, { SideBadge });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/SideBadge.jsx", error: String((e && e.message) || e) }); }

// components/core/Tabs.jsx
try { (() => {
function Tabs({
  items = [],
  value,
  onChange,
  style
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 4,
      font: '500 11px var(--font-mono)',
      ...style
    }
  }, items.map(it => {
    const key = typeof it === 'string' ? it : it.value;
    const label = typeof it === 'string' ? it : it.label;
    const on = key === value;
    return /*#__PURE__*/React.createElement("button", {
      key: key,
      onClick: () => onChange && onChange(key),
      style: {
        font: 'inherit',
        padding: '4px 9px',
        borderRadius: 'var(--radius-xs)',
        border: 0,
        cursor: 'pointer',
        background: on ? 'var(--tint-violet)' : 'none',
        color: on ? 'var(--lilac-200)' : 'var(--text-muted)',
        transition: 'var(--transition-color)'
      }
    }, label);
  }));
}
Object.assign(__ds_scope, { Tabs });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Tabs.jsx", error: String((e && e.message) || e) }); }

// components/core/Tag.jsx
try { (() => {
const tagPalette = {
  violet: {
    background: 'var(--tint-violet)',
    color: 'var(--violet-400)'
  },
  gold: {
    background: 'var(--tint-gold)',
    color: 'var(--gold-400)'
  },
  neutral: {
    background: 'var(--tint-neutral)',
    color: 'var(--text-muted)'
  },
  up: {
    background: 'var(--tint-up)',
    color: 'var(--up-500)'
  },
  down: {
    background: 'var(--tint-down)',
    color: 'var(--down-500)'
  }
};
function Tag({
  tone = 'neutral',
  style,
  children
}) {
  return /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'inline-block',
      font: 'var(--type-badge)',
      letterSpacing: 'var(--track-badge)',
      textTransform: 'uppercase',
      padding: '3px 7px',
      borderRadius: 'var(--radius-xs)',
      ...tagPalette[tone],
      ...style
    }
  }, children);
}
Object.assign(__ds_scope, { Tag });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Tag.jsx", error: String((e && e.message) || e) }); }

// components/feedback/Dialog.jsx
try { (() => {
function Dialog({
  open = true,
  title,
  children,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  onConfirm,
  onCancel,
  danger,
  width = 440
}) {
  if (!open) return null;
  return /*#__PURE__*/React.createElement("div", {
    style: {
      position: 'fixed',
      inset: 0,
      background: 'var(--surface-overlay)',
      display: 'grid',
      placeItems: 'center',
      zIndex: 100
    },
    onClick: onCancel
  }, /*#__PURE__*/React.createElement("div", {
    onClick: e => e.stopPropagation(),
    style: {
      width,
      maxWidth: '90vw',
      background: 'var(--surface-raised)',
      border: '1px solid var(--border-card)',
      borderRadius: 'var(--radius-lg)',
      boxShadow: 'var(--shadow-modal)',
      padding: '22px 24px'
    }
  }, /*#__PURE__*/React.createElement("h3", {
    style: {
      margin: 0,
      font: 'var(--type-h3)',
      color: 'var(--text-heading)'
    }
  }, title), /*#__PURE__*/React.createElement("div", {
    style: {
      margin: '12px 0 20px',
      font: 'var(--type-body)',
      color: 'var(--text-body)'
    }
  }, children), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 10,
      justifyContent: 'flex-end'
    }
  }, cancelLabel && /*#__PURE__*/React.createElement(__ds_scope.Button, {
    variant: "ghost",
    onClick: onCancel
  }, cancelLabel), /*#__PURE__*/React.createElement(__ds_scope.Button, {
    variant: danger ? 'danger' : 'primary',
    onClick: onConfirm
  }, confirmLabel))));
}
Object.assign(__ds_scope, { Dialog });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/feedback/Dialog.jsx", error: String((e && e.message) || e) }); }

// components/feedback/Toast.jsx
try { (() => {
function Toast({
  tone = 'info',
  title,
  children,
  style
}) {
  const edge = {
    info: 'var(--violet-400)',
    success: 'var(--up-500)',
    error: 'var(--down-500)',
    warn: 'var(--gold-400)'
  }[tone];
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 12,
      alignItems: 'flex-start',
      width: 360,
      padding: '13px 16px',
      background: 'var(--surface-raised)',
      border: '1px solid var(--border-card)',
      borderRadius: 'var(--radius-md)',
      boxShadow: 'var(--shadow-card)',
      ...style
    }
  }, /*#__PURE__*/React.createElement("s", {
    style: {
      width: 7,
      height: 7,
      borderRadius: '50%',
      background: edge,
      marginTop: 5,
      flexShrink: 0,
      textDecoration: 'none'
    }
  }), /*#__PURE__*/React.createElement("div", null, title && /*#__PURE__*/React.createElement("b", {
    style: {
      display: 'block',
      font: '500 13.5px var(--font-display)',
      color: 'var(--text-heading)'
    }
  }, title), children && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'block',
      font: '400 12.5px var(--font-body)',
      color: 'var(--text-muted)',
      marginTop: 3
    }
  }, children)));
}
Object.assign(__ds_scope, { Toast });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/feedback/Toast.jsx", error: String((e && e.message) || e) }); }

// components/forms/Checkbox.jsx
try { (() => {
function Checkbox({
  checked,
  onChange,
  label,
  style
}) {
  return /*#__PURE__*/React.createElement("label", {
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 9,
      cursor: 'pointer',
      ...style
    },
    onClick: e => {
      e.preventDefault();
      onChange && onChange(!checked);
    }
  }, /*#__PURE__*/React.createElement("span", {
    "aria-checked": !!checked,
    role: "checkbox",
    style: {
      width: 16,
      height: 16,
      borderRadius: 'var(--radius-xs)',
      border: '1px solid ' + (checked ? 'var(--violet-700)' : 'var(--border-input)'),
      background: checked ? 'var(--violet-700)' : 'var(--surface-sunken)',
      display: 'grid',
      placeItems: 'center',
      color: '#fff',
      font: '600 10px var(--font-mono)',
      transition: 'var(--transition-color)'
    }
  }, checked ? '✓' : ''), label && /*#__PURE__*/React.createElement("span", {
    style: {
      font: '400 13.5px var(--font-body)',
      color: 'var(--text-body)'
    }
  }, label));
}
Object.assign(__ds_scope, { Checkbox });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Checkbox.jsx", error: String((e && e.message) || e) }); }

// components/forms/Input.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
function Input({
  label,
  hint,
  mono,
  style,
  inputStyle,
  ...rest
}) {
  const [focus, setFocus] = React.useState(false);
  return /*#__PURE__*/React.createElement("label", {
    style: {
      display: 'block',
      ...style
    }
  }, label && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'block',
      font: 'var(--type-eyebrow)',
      letterSpacing: 'var(--track-eyebrow)',
      textTransform: 'uppercase',
      color: 'var(--text-muted)',
      marginBottom: 7
    }
  }, label), /*#__PURE__*/React.createElement("input", _extends({}, rest, {
    onFocus: e => {
      setFocus(true);
      rest.onFocus && rest.onFocus(e);
    },
    onBlur: e => {
      setFocus(false);
      rest.onBlur && rest.onBlur(e);
    },
    style: {
      width: '100%',
      height: 'var(--control-h)',
      padding: '0 14px',
      background: 'var(--surface-sunken)',
      border: `1px solid ${focus ? 'var(--violet-400)' : 'var(--border-input)'}`,
      borderRadius: 'var(--radius-sm)',
      color: 'var(--text-heading)',
      font: mono ? '400 13px var(--font-mono)' : '400 14px var(--font-body)',
      outline: 'none',
      transition: 'var(--transition-color)',
      ...inputStyle
    }
  })), hint && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'block',
      font: '400 11px var(--font-mono)',
      color: 'var(--text-faint)',
      marginTop: 6
    }
  }, hint));
}
Object.assign(__ds_scope, { Input });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Input.jsx", error: String((e && e.message) || e) }); }

// components/forms/Select.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
function Select({
  label,
  options = [],
  style,
  selectStyle,
  ...rest
}) {
  return /*#__PURE__*/React.createElement("label", {
    style: {
      display: 'block',
      ...style
    }
  }, label && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'block',
      font: 'var(--type-eyebrow)',
      letterSpacing: 'var(--track-eyebrow)',
      textTransform: 'uppercase',
      color: 'var(--text-muted)',
      marginBottom: 7
    }
  }, label), /*#__PURE__*/React.createElement("span", {
    style: {
      position: 'relative',
      display: 'block'
    }
  }, /*#__PURE__*/React.createElement("select", _extends({}, rest, {
    style: {
      width: '100%',
      height: 'var(--control-h)',
      padding: '0 34px 0 14px',
      background: 'var(--surface-sunken)',
      border: '1px solid var(--border-input)',
      borderRadius: 'var(--radius-sm)',
      color: 'var(--text-heading)',
      font: '400 14px var(--font-body)',
      outline: 'none',
      appearance: 'none',
      WebkitAppearance: 'none',
      cursor: 'pointer',
      ...selectStyle
    }
  }), options.map(o => typeof o === 'string' ? /*#__PURE__*/React.createElement("option", {
    key: o,
    value: o
  }, o) : /*#__PURE__*/React.createElement("option", {
    key: o.value,
    value: o.value
  }, o.label))), /*#__PURE__*/React.createElement("span", {
    style: {
      position: 'absolute',
      right: 13,
      top: '50%',
      transform: 'translateY(-50%)',
      color: 'var(--text-muted)',
      fontSize: 10,
      pointerEvents: 'none'
    }
  }, "\u25BE")));
}
Object.assign(__ds_scope, { Select });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Select.jsx", error: String((e && e.message) || e) }); }

// components/forms/Switch.jsx
try { (() => {
function Switch({
  checked,
  onChange,
  label,
  style
}) {
  return /*#__PURE__*/React.createElement("label", {
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 10,
      cursor: 'pointer',
      ...style
    }
  }, /*#__PURE__*/React.createElement("button", {
    role: "switch",
    "aria-checked": !!checked,
    onClick: () => onChange && onChange(!checked),
    style: {
      width: 36,
      height: 20,
      padding: 2,
      border: '1px solid ' + (checked ? 'var(--violet-700)' : 'var(--border-input)'),
      borderRadius: 999,
      background: checked ? 'var(--violet-700)' : 'var(--surface-sunken)',
      cursor: 'pointer',
      transition: 'var(--transition-color)',
      display: 'flex',
      justifyContent: checked ? 'flex-end' : 'flex-start'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 14,
      height: 14,
      borderRadius: '50%',
      background: checked ? '#fff' : 'var(--lilac-500)',
      transition: 'background var(--dur-fast) var(--ease)'
    }
  })), label && /*#__PURE__*/React.createElement("span", {
    style: {
      font: '400 13.5px var(--font-body)',
      color: 'var(--text-body)'
    }
  }, label));
}
Object.assign(__ds_scope, { Switch });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Switch.jsx", error: String((e && e.message) || e) }); }

// components/navigation/Wordmark.jsx
try { (() => {
function Wordmark({
  size = 16,
  descriptor = false,
  style
}) {
  return /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'inline-flex',
      flexDirection: 'column',
      ...style
    }
  }, /*#__PURE__*/React.createElement("b", {
    style: {
      font: `600 ${size}px/1.05 var(--font-display)`,
      color: 'var(--text-heading)',
      letterSpacing: '-.01em',
      whiteSpace: 'nowrap'
    }
  }, "CryptoSmith ", /*#__PURE__*/React.createElement("i", {
    style: {
      fontStyle: 'normal',
      color: 'var(--violet-400)'
    }
  }, "X")), descriptor && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      font: `500 ${Math.round(size * .4)}px var(--font-mono)`,
      letterSpacing: '.1em',
      color: 'var(--text-muted)',
      marginTop: Math.max(3, size * .12)
    }
  }, /*#__PURE__*/React.createElement("span", null, "PERPS & CRYPTO"), /*#__PURE__*/React.createElement("span", null, "TRADE BOT")));
}
Object.assign(__ds_scope, { Wordmark });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/navigation/Wordmark.jsx", error: String((e && e.message) || e) }); }

// components/navigation/TopNav.jsx
try { (() => {
function TopNav({
  items = [],
  active,
  onNavigate,
  user,
  live = 'LIVE',
  markSrc,
  style
}) {
  return /*#__PURE__*/React.createElement("header", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 28,
      padding: '15px 30px',
      borderBottom: '1px solid var(--border-card)',
      background: 'var(--surface-page)',
      ...style
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 11
    }
  }, markSrc && /*#__PURE__*/React.createElement("img", {
    src: markSrc,
    width: "28",
    height: "28",
    alt: ""
  }), /*#__PURE__*/React.createElement(__ds_scope.Wordmark, {
    size: 16
  })), /*#__PURE__*/React.createElement("nav", {
    style: {
      display: 'flex',
      gap: 24,
      font: 'var(--type-nav)',
      letterSpacing: 'var(--track-nav)',
      textTransform: 'uppercase'
    }
  }, items.map(it => {
    const on = it === active;
    return /*#__PURE__*/React.createElement("a", {
      key: it,
      href: "#",
      onClick: e => {
        e.preventDefault();
        onNavigate && onNavigate(it);
      },
      style: {
        color: on ? 'var(--lilac-100)' : 'var(--text-muted)',
        padding: '4px 0',
        borderBottom: on ? '2px solid var(--gold-400)' : '2px solid transparent'
      }
    }, it);
  })), /*#__PURE__*/React.createElement("span", {
    style: {
      marginLeft: 'auto',
      display: 'flex',
      alignItems: 'center',
      gap: 16
    }
  }, live && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 7,
      font: '600 10px var(--font-mono)',
      letterSpacing: '.18em',
      color: 'var(--live)'
    }
  }, /*#__PURE__*/React.createElement("s", {
    style: {
      width: 7,
      height: 7,
      borderRadius: '50%',
      background: 'var(--live)',
      boxShadow: 'var(--shadow-live)',
      textDecoration: 'none'
    }
  }), live), user && /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 9,
      padding: '6px 12px 6px 7px',
      border: '1px solid var(--border-input)',
      borderRadius: 'var(--radius-sm)',
      font: '500 12px var(--font-mono)',
      color: 'var(--lilac-200)'
    }
  }, /*#__PURE__*/React.createElement("img", {
    src: "../../assets/cryptosmith-coin.svg",
    width: "20",
    height: "20",
    alt: ""
  }), user)));
}
Object.assign(__ds_scope, { TopNav });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/navigation/TopNav.jsx", error: String((e && e.message) || e) }); }

// components/trading/EquityCurve.jsx
try { (() => {
function EquityCurve({
  points = [4, 14, 8, 34, 28, 54, 46, 75, 65, 97, 87, 121, 113, 140],
  height = 240,
  showFill = true,
  style
}) {
  const w = 800,
    h = height,
    pad = 12;
  const min = Math.min(...points),
    max = Math.max(...points);
  const xy = points.map((p, i) => [i / (points.length - 1) * w, h - pad - (p - min) / (max - min || 1) * (h - pad * 2)]);
  const line = xy.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`).join(' ');
  const uid = React.useId().replace(/:/g, '');
  return /*#__PURE__*/React.createElement("svg", {
    viewBox: `0 0 ${w} ${h}`,
    preserveAspectRatio: "none",
    style: {
      display: 'block',
      width: '100%',
      height: 'auto',
      ...style
    }
  }, /*#__PURE__*/React.createElement("defs", null, /*#__PURE__*/React.createElement("linearGradient", {
    id: `s${uid}`,
    x1: "0",
    y1: "0",
    x2: "1",
    y2: "0"
  }, /*#__PURE__*/React.createElement("stop", {
    offset: "0",
    stopColor: "#F5B84F"
  }), /*#__PURE__*/React.createElement("stop", {
    offset: ".55",
    stopColor: "#C98F63"
  }), /*#__PURE__*/React.createElement("stop", {
    offset: "1",
    stopColor: "#6B4EDB"
  })), /*#__PURE__*/React.createElement("linearGradient", {
    id: `f${uid}`,
    x1: "0",
    y1: "0",
    x2: "0",
    y2: "1"
  }, /*#__PURE__*/React.createElement("stop", {
    offset: "0",
    stopColor: "#8C6BC9",
    stopOpacity: ".22"
  }), /*#__PURE__*/React.createElement("stop", {
    offset: "1",
    stopColor: "#8C6BC9",
    stopOpacity: "0"
  }))), showFill && /*#__PURE__*/React.createElement("path", {
    d: `${line} L${w} ${h} L0 ${h} Z`,
    fill: `url(#f${uid})`
  }), /*#__PURE__*/React.createElement("path", {
    d: line,
    fill: "none",
    stroke: `url(#s${uid})`,
    strokeWidth: "2.5"
  }));
}
Object.assign(__ds_scope, { EquityCurve });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/trading/EquityCurve.jsx", error: String((e && e.message) || e) }); }

// components/trading/PositionsTable.jsx
try { (() => {
const th = {
  font: '500 10px var(--font-mono)',
  letterSpacing: '.16em',
  textTransform: 'uppercase',
  color: 'var(--text-muted)',
  textAlign: 'left',
  padding: '12px 20px',
  borderBottom: '1px solid var(--border-hairline)'
};
const td = {
  padding: '12px 20px',
  borderBottom: '1px solid rgba(161,138,255,.06)',
  color: 'var(--text-body)',
  font: '400 12.5px var(--font-mono)'
};
const num = {
  textAlign: 'right'
};
function PositionsTable({
  rows = [],
  style
}) {
  return /*#__PURE__*/React.createElement("table", {
    style: {
      width: '100%',
      borderCollapse: 'collapse',
      ...style
    }
  }, /*#__PURE__*/React.createElement("thead", null, /*#__PURE__*/React.createElement("tr", null, /*#__PURE__*/React.createElement("th", {
    style: th
  }, "Market"), /*#__PURE__*/React.createElement("th", {
    style: th
  }, "Venue"), /*#__PURE__*/React.createElement("th", {
    style: th
  }, "Side"), /*#__PURE__*/React.createElement("th", {
    style: {
      ...th,
      ...num
    }
  }, "Size"), /*#__PURE__*/React.createElement("th", {
    style: {
      ...th,
      ...num
    }
  }, "Entry"), /*#__PURE__*/React.createElement("th", {
    style: {
      ...th,
      ...num
    }
  }, "Mark"), /*#__PURE__*/React.createElement("th", {
    style: {
      ...th,
      ...num
    }
  }, "uPnL"))), /*#__PURE__*/React.createElement("tbody", null, rows.map((r, i) => {
    const up = String(r.upnl).trim().startsWith('+');
    const last = i === rows.length - 1;
    const cell = last ? {
      ...td,
      borderBottom: 0
    } : td;
    return /*#__PURE__*/React.createElement("tr", {
      key: r.market + i
    }, /*#__PURE__*/React.createElement("td", {
      style: cell
    }, /*#__PURE__*/React.createElement("b", {
      style: {
        color: 'var(--text-heading)',
        fontWeight: 500
      }
    }, r.market)), /*#__PURE__*/React.createElement("td", {
      style: cell
    }, r.venue), /*#__PURE__*/React.createElement("td", {
      style: cell
    }, /*#__PURE__*/React.createElement(__ds_scope.SideBadge, {
      side: r.side
    })), /*#__PURE__*/React.createElement("td", {
      style: {
        ...cell,
        ...num
      }
    }, r.size), /*#__PURE__*/React.createElement("td", {
      style: {
        ...cell,
        ...num
      }
    }, r.entry), /*#__PURE__*/React.createElement("td", {
      style: {
        ...cell,
        ...num
      }
    }, r.mark), /*#__PURE__*/React.createElement("td", {
      style: {
        ...cell,
        ...num,
        color: up ? 'var(--pnl-up)' : 'var(--pnl-down)',
        fontWeight: 500
      }
    }, r.upnl));
  })));
}
Object.assign(__ds_scope, { PositionsTable });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/trading/PositionsTable.jsx", error: String((e && e.message) || e) }); }

// components/trading/StrategyCard.jsx
try { (() => {
function StrategyCard({
  name,
  status = 'running',
  ai,
  metrics = [],
  last,
  style
}) {
  const statusTag = {
    running: ['gold', 'RUNNING'],
    paused: ['neutral', 'PAUSED'],
    stopped: ['down', 'STOPPED']
  }[status] || ['neutral', status];
  return /*#__PURE__*/React.createElement("div", {
    style: {
      padding: '16px 18px',
      borderBottom: last ? 0 : '1px solid var(--border-hairline)',
      ...style
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'baseline',
      gap: 9,
      flexWrap: 'wrap'
    }
  }, /*#__PURE__*/React.createElement("b", {
    style: {
      font: '500 14.5px var(--font-display)',
      color: 'var(--text-heading)'
    }
  }, name), /*#__PURE__*/React.createElement(__ds_scope.Tag, {
    tone: statusTag[0]
  }, statusTag[1]), ai && /*#__PURE__*/React.createElement(__ds_scope.Tag, {
    tone: "violet"
  }, "AI WATCHLIST")), metrics.length > 0 && /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 16,
      marginTop: 9,
      font: '400 11.5px var(--font-mono)',
      color: 'var(--text-muted)'
    }
  }, metrics.map((m, i) => /*#__PURE__*/React.createElement("span", {
    key: i
  }, m.label, " ", /*#__PURE__*/React.createElement("b", {
    style: {
      color: m.tone === 'up' ? 'var(--pnl-up)' : m.tone === 'down' ? 'var(--pnl-down)' : 'var(--text-data)',
      fontWeight: 500
    }
  }, m.value)))));
}
Object.assign(__ds_scope, { StrategyCard });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/trading/StrategyCard.jsx", error: String((e && e.message) || e) }); }

// components/trading/VenueStatus.jsx
try { (() => {
function VenueStatus({
  venues = [],
  style
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 12,
      ...style
    }
  }, venues.map(v => /*#__PURE__*/React.createElement("span", {
    key: v.name,
    style: {
      flex: 1,
      display: 'flex',
      alignItems: 'center',
      gap: 8,
      font: '500 11.5px var(--font-mono)',
      color: 'var(--text-body)'
    }
  }, /*#__PURE__*/React.createElement("s", {
    style: {
      width: 6,
      height: 6,
      borderRadius: '50%',
      background: v.ok === false ? 'var(--status-off)' : 'var(--status-ok)',
      textDecoration: 'none'
    }
  }), String(v.name).toUpperCase(), /*#__PURE__*/React.createElement("em", {
    style: {
      fontStyle: 'normal',
      color: 'var(--text-muted)',
      marginLeft: 'auto'
    }
  }, v.latency || '—'))));
}
Object.assign(__ds_scope, { VenueStatus });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/trading/VenueStatus.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/screens-dashboard.jsx
try { (() => {
function _extends() { return _extends = Object.assign ? Object.assign.bind() : function (n) { for (var e = 1; e < arguments.length; e++) { var t = arguments[e]; for (var r in t) ({}).hasOwnProperty.call(t, r) && (n[r] = t[r]); } return n; }, _extends.apply(null, arguments); }
window.CSX_DATA = {
  kpis: [{
    label: 'Equity',
    value: '$128,430',
    delta: '↑ +$1,284.10 · 24h',
    deltaTone: 'up'
  }, {
    label: 'Unrealized PnL',
    value: '+$842.55',
    delta: '↑ +0.66%',
    deltaTone: 'up'
  }, {
    label: 'Open positions',
    value: '7',
    delta: '4 long · 3 short',
    deltaTone: 'muted'
  }, {
    label: 'Exposure',
    value: '42%',
    delta: 'limit 60%',
    deltaTone: 'muted'
  }, {
    label: 'Win rate · 30d',
    value: '61.4%',
    delta: '212 trades',
    deltaTone: 'muted'
  }],
  venues: [{
    name: 'Kraken',
    latency: '142ms'
  }, {
    name: 'Binance',
    latency: '96ms'
  }, {
    name: 'WEEX',
    ok: false
  }, {
    name: 'Hyperliquid',
    latency: '88ms'
  }],
  strategies: [{
    name: 'Momentum Perps v3',
    status: 'running',
    ai: true,
    metrics: [{
      label: 'PnL 30d',
      value: '+4.8%',
      tone: 'up'
    }, {
      label: 'DD',
      value: '−2.1%'
    }, {
      label: 'trades',
      value: '96'
    }]
  }, {
    name: 'Grid BTC/USD',
    status: 'running',
    metrics: [{
      label: 'PnL 30d',
      value: '+1.9%',
      tone: 'up'
    }, {
      label: 'DD',
      value: '−0.8%'
    }, {
      label: 'trades',
      value: '301'
    }]
  }, {
    name: 'Funding Harvest',
    status: 'paused',
    metrics: [{
      label: 'PnL 30d',
      value: '−0.3%',
      tone: 'down'
    }, {
      label: 'DD',
      value: '−1.2%'
    }, {
      label: 'trades',
      value: '44'
    }]
  }],
  positions: [{
    market: 'BTC-PERP',
    venue: 'Hyperliquid',
    side: 'long',
    size: '0.42 BTC',
    entry: '96,410.00',
    mark: '98,102.50',
    upnl: '+$710.85'
  }, {
    market: 'ETH-PERP',
    venue: 'Hyperliquid',
    side: 'short',
    size: '6.0 ETH',
    entry: '4,821.00',
    mark: '4,760.20',
    upnl: '+$364.80'
  }, {
    market: 'SOL/USD',
    venue: 'Kraken',
    side: 'long',
    size: '180 SOL',
    entry: '231.40',
    mark: '228.95',
    upnl: '−$441.00'
  }, {
    market: 'XRP/USDT',
    venue: 'Binance',
    side: 'long',
    size: '9,400 XRP',
    entry: '2.841',
    mark: '2.863',
    upnl: '+$206.80'
  }],
  curve: [100, 102, 101, 105, 104, 109, 108, 114, 112, 119, 117, 124, 122, 130]
};
window.CSXDashboard = function CSXDashboard() {
  const {
    KpiTile,
    Card,
    Tabs,
    EquityCurve,
    VenueStatus,
    StrategyCard,
    PositionsTable,
    Button
  } = window.CryptoSmithXDesignSystem_d88f99;
  const [range, setRange] = React.useState('1W');
  const d = window.CSX_DATA;
  return /*#__PURE__*/React.createElement("main", {
    style: {
      padding: '24px 30px',
      display: 'grid',
      gap: 20
    }
  }, /*#__PURE__*/React.createElement("section", {
    style: {
      display: 'grid',
      gridTemplateColumns: 'repeat(5,1fr)',
      gap: 14
    }
  }, d.kpis.map(k => /*#__PURE__*/React.createElement(KpiTile, _extends({
    key: k.label
  }, k)))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: 'minmax(0,1fr) 340px',
      gap: 20
    }
  }, /*#__PURE__*/React.createElement(Card, {
    title: "Equity curve",
    pad: false,
    actions: /*#__PURE__*/React.createElement(Tabs, {
      items: ['1D', '1W', '1M', 'ALL'],
      value: range,
      onChange: setRange
    })
  }, /*#__PURE__*/React.createElement(EquityCurve, {
    points: d.curve,
    height: 240,
    style: {
      padding: '14px 20px 0'
    }
  }), /*#__PURE__*/React.createElement(VenueStatus, {
    venues: d.venues,
    style: {
      padding: '14px 20px',
      borderTop: '1px solid var(--border-hairline)',
      marginTop: 8
    }
  })), /*#__PURE__*/React.createElement(Card, {
    title: "Strategies",
    pad: false
  }, d.strategies.map((s, i) => /*#__PURE__*/React.createElement(StrategyCard, _extends({
    key: s.name
  }, s))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 10,
      padding: '16px 18px'
    }
  }, /*#__PURE__*/React.createElement(Button, null, "New strategy"), /*#__PURE__*/React.createElement(Button, {
    variant: "ghost"
  }, "Backtest")))), /*#__PURE__*/React.createElement(Card, {
    title: "Open positions",
    pad: false
  }, /*#__PURE__*/React.createElement(PositionsTable, {
    rows: d.positions
  })));
};
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/screens-dashboard.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/screens-login.jsx
try { (() => {
const csxLoginStyles = {
  wrap: {
    minHeight: '100vh',
    display: 'grid',
    placeItems: 'center',
    background: 'var(--wash-gold), var(--wash-violet), var(--surface-page)'
  }
};
window.CSXLogin = function CSXLogin({
  onLogin
}) {
  const {
    Wordmark,
    Input,
    Button,
    Checkbox
  } = window.CryptoSmithXDesignSystem_d88f99;
  const [remember, setRemember] = React.useState(true);
  return /*#__PURE__*/React.createElement("div", {
    style: csxLoginStyles.wrap
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      width: 400,
      maxWidth: '92vw'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      marginBottom: 28
    }
  }, /*#__PURE__*/React.createElement("img", {
    src: "../../assets/cryptosmith-mark.svg",
    width: "56",
    height: "56",
    alt: "",
    style: {
      marginBottom: 16
    }
  }), /*#__PURE__*/React.createElement(Wordmark, {
    size: 30,
    descriptor: true
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      background: 'var(--surface-card)',
      border: '1px solid var(--border-card)',
      borderRadius: 'var(--radius-lg)',
      padding: '26px 26px 24px',
      display: 'flex',
      flexDirection: 'column',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement(Input, {
    label: "Email",
    placeholder: "you@example.com"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Password",
    type: "password",
    placeholder: "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022"
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between'
    }
  }, /*#__PURE__*/React.createElement(Checkbox, {
    checked: remember,
    onChange: setRemember,
    label: "Remember me"
  }), /*#__PURE__*/React.createElement("a", {
    href: "#",
    onClick: e => e.preventDefault(),
    style: {
      font: '400 12.5px var(--font-body)'
    }
  }, "Forgot?")), /*#__PURE__*/React.createElement(Button, {
    size: "lg",
    onClick: onLogin,
    style: {
      width: '100%'
    }
  }, "Sign in")), /*#__PURE__*/React.createElement("p", {
    style: {
      textAlign: 'center',
      marginTop: 18,
      font: '400 11px var(--font-mono)',
      letterSpacing: '.08em',
      color: 'var(--text-faint)'
    }
  }, "MULTI-USER \xB7 ADMIN / TRADER / VIEWER")));
};
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/screens-login.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/screens-settings.jsx
try { (() => {
window.CSXSettings = function CSXSettings() {
  const {
    Card,
    Input,
    Select,
    Switch,
    Button,
    Tag
  } = window.CryptoSmithXDesignSystem_d88f99;
  const [notif, setNotif] = React.useState(true);
  const [twofa, setTwofa] = React.useState(true);
  const keys = [{
    venue: 'KRAKEN',
    key: 'krkn_a81f········9c2e',
    scope: 'trade',
    ok: true
  }, {
    venue: 'BINANCE',
    key: 'bnc_77d3········01aa',
    scope: 'trade',
    ok: true
  }, {
    venue: 'WEEX',
    key: '—',
    scope: '—',
    ok: false
  }, {
    venue: 'HYPERLIQUID',
    key: '0x4c9a········e11f',
    scope: 'trade',
    ok: true
  }];
  const team = [{
    name: 'd.bykovas',
    role: 'Admin'
  }, {
    name: 'l.peciukonis',
    role: 'Admin'
  }, {
    name: 'guest.viewer',
    role: 'Viewer'
  }];
  const rowFont = {
    font: '400 12.5px var(--font-mono)',
    color: 'var(--text-body)'
  };
  return /*#__PURE__*/React.createElement("main", {
    style: {
      padding: '24px 30px',
      display: 'grid',
      gridTemplateColumns: '1fr 1fr',
      gap: 20,
      alignItems: 'start',
      maxWidth: 1160
    }
  }, /*#__PURE__*/React.createElement(Card, {
    title: "Profile"
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement(Input, {
    label: "Display name",
    defaultValue: "d.bykovas"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Email",
    defaultValue: "denisas@blynai.eu"
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 28
    }
  }, /*#__PURE__*/React.createElement(Switch, {
    checked: twofa,
    onChange: setTwofa,
    label: "Two-factor auth"
  }), /*#__PURE__*/React.createElement(Switch, {
    checked: notif,
    onChange: setNotif,
    label: "Fill notifications"
  })))), /*#__PURE__*/React.createElement(Card, {
    title: "Risk limits"
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: '1fr 1fr',
      gap: 16
    }
  }, /*#__PURE__*/React.createElement(Input, {
    label: "Max exposure",
    mono: true,
    defaultValue: "60%"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Max daily loss",
    mono: true,
    defaultValue: "\u22123.0%"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Max leverage",
    mono: true,
    defaultValue: "5\xD7"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Per-market cap",
    mono: true,
    defaultValue: "$25,000"
  }))), /*#__PURE__*/React.createElement(Card, {
    title: "API keys",
    pad: false
  }, keys.map((k, i) => /*#__PURE__*/React.createElement("div", {
    key: k.venue,
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 14,
      padding: '13px 20px',
      borderBottom: i < keys.length - 1 ? '1px solid var(--border-hairline)' : 0
    }
  }, /*#__PURE__*/React.createElement("b", {
    style: {
      font: '500 11.5px var(--font-mono)',
      letterSpacing: '.08em',
      color: 'var(--text-heading)',
      width: 100
    }
  }, k.venue), /*#__PURE__*/React.createElement("span", {
    style: rowFont
  }, k.key), /*#__PURE__*/React.createElement("span", {
    style: {
      marginLeft: 'auto'
    }
  }, k.ok ? /*#__PURE__*/React.createElement(Tag, {
    tone: "gold"
  }, "Connected") : /*#__PURE__*/React.createElement(Tag, {
    tone: "neutral"
  }, "Not set")), /*#__PURE__*/React.createElement(Button, {
    variant: "quiet",
    size: "sm"
  }, k.ok ? 'Rotate' : 'Add key')))), /*#__PURE__*/React.createElement(Card, {
    title: "Team",
    pad: false
  }, team.map((m, i) => /*#__PURE__*/React.createElement("div", {
    key: m.name,
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 12,
      padding: '11px 20px',
      borderBottom: i < team.length - 1 ? '1px solid var(--border-hairline)' : 0
    }
  }, /*#__PURE__*/React.createElement("img", {
    src: "../../assets/cryptosmith-coin.svg",
    width: "22",
    height: "22",
    alt: ""
  }), /*#__PURE__*/React.createElement("span", {
    style: rowFont
  }, m.name), /*#__PURE__*/React.createElement("span", {
    style: {
      marginLeft: 'auto',
      width: 120
    }
  }, /*#__PURE__*/React.createElement(Select, {
    options: ['Admin', 'Trader', 'Viewer'],
    defaultValue: m.role
  })))), /*#__PURE__*/React.createElement("div", {
    style: {
      padding: '14px 20px'
    }
  }, /*#__PURE__*/React.createElement(Button, {
    variant: "ghost",
    size: "sm"
  }, "Invite member"))));
};
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/screens-settings.jsx", error: String((e && e.message) || e) }); }

// ui_kits/console/screens-strategies.jsx
try { (() => {
window.CSXStrategies = function CSXStrategies() {
  const {
    Card,
    Tag,
    Button,
    Input,
    Select,
    Switch,
    StrategyCard,
    EquityCurve,
    Dialog
  } = window.CryptoSmithXDesignSystem_d88f99;
  const [sel, setSel] = React.useState('Momentum Perps v3');
  const [closeOnly, setCloseOnly] = React.useState(false);
  const [reduce, setReduce] = React.useState(true);
  const [confirm, setConfirm] = React.useState(false);
  const d = window.CSX_DATA;
  return /*#__PURE__*/React.createElement("main", {
    style: {
      padding: '24px 30px',
      display: 'grid',
      gridTemplateColumns: '380px minmax(0,1fr)',
      gap: 20,
      alignItems: 'start'
    }
  }, /*#__PURE__*/React.createElement(Card, {
    title: "Strategies",
    pad: false
  }, d.strategies.map(s => /*#__PURE__*/React.createElement("div", {
    key: s.name,
    onClick: () => setSel(s.name),
    style: {
      cursor: 'pointer',
      background: sel === s.name ? 'var(--surface-raised)' : 'none'
    }
  }, /*#__PURE__*/React.createElement(StrategyCard, s))), /*#__PURE__*/React.createElement("div", {
    style: {
      padding: '16px 18px'
    }
  }, /*#__PURE__*/React.createElement(Button, {
    style: {
      width: '100%'
    }
  }, "New strategy"))), /*#__PURE__*/React.createElement(Card, {
    title: sel,
    pad: false,
    actions: /*#__PURE__*/React.createElement(React.Fragment, null, /*#__PURE__*/React.createElement(Tag, {
      tone: "gold"
    }, "Running"), /*#__PURE__*/React.createElement(Tag, {
      tone: "violet"
    }, "AI watchlist"))
  }, /*#__PURE__*/React.createElement(EquityCurve, {
    points: [100, 103, 101, 107, 106, 112, 110, 118, 115, 121],
    height: 140,
    style: {
      padding: '14px 20px 0'
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: '1fr 1fr 1fr',
      gap: 16,
      padding: '18px 20px',
      borderTop: '1px solid var(--border-hairline)',
      marginTop: 10
    }
  }, /*#__PURE__*/React.createElement(Select, {
    label: "Venue",
    options: ['Hyperliquid', 'Kraken', 'Binance', 'WEEX'],
    defaultValue: "Hyperliquid"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Max position",
    mono: true,
    defaultValue: "0.50 BTC"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Leverage",
    mono: true,
    defaultValue: "3\xD7"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Stop loss",
    mono: true,
    defaultValue: "\u22122.5%"
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Take profit",
    mono: true,
    defaultValue: "+6.0%"
  }), /*#__PURE__*/React.createElement(Select, {
    label: "Cycle",
    options: ['60 s', '120 s', '300 s'],
    defaultValue: "120 s"
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 28,
      padding: '0 20px 18px'
    }
  }, /*#__PURE__*/React.createElement(Switch, {
    checked: closeOnly,
    onChange: setCloseOnly,
    label: "Trade on close only"
  }), /*#__PURE__*/React.createElement(Switch, {
    checked: reduce,
    onChange: setReduce,
    label: "Reduce-only after drawdown"
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 10,
      padding: '16px 20px',
      borderTop: '1px solid var(--border-hairline)'
    }
  }, /*#__PURE__*/React.createElement(Button, null, "Save changes"), /*#__PURE__*/React.createElement(Button, {
    variant: "ghost"
  }, "Run backtest"), /*#__PURE__*/React.createElement(Button, {
    variant: "danger",
    style: {
      marginLeft: 'auto'
    },
    onClick: () => setConfirm(true)
  }, "Stop strategy"))), confirm && /*#__PURE__*/React.createElement(Dialog, {
    open: true,
    title: "Stop strategy?",
    danger: true,
    confirmLabel: "Stop",
    onConfirm: () => setConfirm(false),
    onCancel: () => setConfirm(false)
  }, "Open positions stay open; the bot just stops managing them."));
};
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/console/screens-strategies.jsx", error: String((e && e.message) || e) }); }

__ds_ns.Button = __ds_scope.Button;

__ds_ns.Card = __ds_scope.Card;

__ds_ns.KpiTile = __ds_scope.KpiTile;

__ds_ns.SideBadge = __ds_scope.SideBadge;

__ds_ns.Tabs = __ds_scope.Tabs;

__ds_ns.Tag = __ds_scope.Tag;

__ds_ns.Dialog = __ds_scope.Dialog;

__ds_ns.Toast = __ds_scope.Toast;

__ds_ns.Checkbox = __ds_scope.Checkbox;

__ds_ns.Input = __ds_scope.Input;

__ds_ns.Select = __ds_scope.Select;

__ds_ns.Switch = __ds_scope.Switch;

__ds_ns.TopNav = __ds_scope.TopNav;

__ds_ns.Wordmark = __ds_scope.Wordmark;

__ds_ns.EquityCurve = __ds_scope.EquityCurve;

__ds_ns.PositionsTable = __ds_scope.PositionsTable;

__ds_ns.StrategyCard = __ds_scope.StrategyCard;

__ds_ns.VenueStatus = __ds_scope.VenueStatus;

})();
