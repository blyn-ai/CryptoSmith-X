Sits under every figure and states how old the call behind it is.

```jsx
<AgeLine seconds={3} />                      {/* 3 s ago */}
<AgeLine seconds={94} />                     {/* △ 94 s ago, in the alarm ink */}
<AgeLine seconds={480} />                    {/* △ degraded — no figure worth counting */}
<AgeLine seconds={null} missing />           {/* — */}
```

Pair it with the same clock that drives the figure's `opacity`: the figure fades, the age
does not.
