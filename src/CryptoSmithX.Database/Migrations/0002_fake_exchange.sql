-- The in-process venue used by the scaffold and by local runs. Guarded so the four real
-- exchanges inserted by 0001 are untouched.
insert into exchange (code, name, is_enabled) values
    ('fake', 'Fake exchange (in-process)', true)
on conflict (code) do nothing;
