// Shared live hint for the LDAP "channel security" selector.
//
// Every screen that configures an AD connection needs to explain what the chosen
// mode + port will ACTUALLY do, because the failure is silent: an unencrypted or
// contradictory channel still reads fine and only dies when a password is written
// (WILL_NOT_PERFORM). Keeping this in one file so the four screens can't drift —
// drifting copies are what caused that bug in the first place.
//
// Mode values mirror Core.Enums.LdapSecurityMode:
//   0 Auto | 1 SignAndSeal | 2 Ldaps | 3 StartTls | 4 None
(function () {
    var TLS_PORTS = [636, 3269];

    function describe(mode, port, rtl) {
        var tls = TLS_PORTS.indexOf(port) !== -1;
        mode = String(mode);

        if (mode === '0') {
            return tls
                ? { cls: 'alert-secondary', msg: rtl ? 'تلقائي: المنفذ ' + port + ' ⇒ LDAPS/TLS.' : 'Auto: port ' + port + ' ⇒ LDAPS/TLS.' }
                : { cls: 'alert-secondary', msg: rtl ? 'تلقائي: المنفذ ' + port + ' ⇒ Kerberos sign & seal (مشفّر، بلا شهادة).' : 'Auto: port ' + port + ' ⇒ Kerberos sign & seal (encrypted, no certificate).' };
        }
        if (mode === '1') {
            return tls
                ? { cls: 'alert-warning', msg: rtl ? 'تنبيه: المنفذ ' + port + ' منفذ TLS — sign & seal لن ينجح عليه.' : 'Warning: port ' + port + ' is a TLS port — sign & seal will not work there.' }
                : { cls: 'alert-secondary', msg: rtl ? 'مشفّر عبر Kerberos، لا يحتاج شهادة. الأنسب للمنفذ 389.' : 'Encrypted via Kerberos, no certificate needed. Best for port 389.' };
        }
        if (mode === '2') {
            return tls
                ? { cls: 'alert-secondary', msg: rtl ? 'يتطلب شهادة صالحة على الـ DC.' : 'Requires a valid certificate on the DC.' }
                : { cls: 'alert-danger', msg: rtl ? '⚠️ LDAPS مع المنفذ ' + port + ' تركيبة متناقضة — الاتصال سيفشل. استخدم 636، أو اختر sign & seal للمنفذ ' + port + '.' : '⚠️ LDAPS with port ' + port + ' is contradictory — the connection will fail. Use 636, or pick sign & seal for port ' + port + '.' };
        }
        if (mode === '3') {
            return tls
                ? { cls: 'alert-warning', msg: rtl ? 'تنبيه: StartTLS يُستخدم على المنفذ العادي (389) لا ' + port + '.' : 'Warning: StartTLS is for the plain port (389), not ' + port + '.' }
                : { cls: 'alert-secondary', msg: rtl ? 'يتصل على المنفذ العادي ثم يرقّي إلى TLS — يتطلب شهادة على الـ DC.' : 'Connects on the plain port then upgrades to TLS — requires a certificate on the DC.' };
        }
        if (mode === '4') {
            return { cls: 'alert-danger', msg: rtl ? '⚠️ بلا تشفير: القراءة تعمل، لكن AD سيرفض كتابة كلمة المرور (WILL_NOT_PERFORM).' : '⚠️ No encryption: reads work, but AD will refuse the password write (WILL_NOT_PERFORM).' };
        }
        return { cls: 'alert-secondary', msg: '' };
    }

    // opts: { mode, port, hint } = element ids, rtl = boolean
    window.bindLdapModeHint = function (opts) {
        var modeEl = document.getElementById(opts.mode);
        var portEl = document.getElementById(opts.port);
        var hintEl = document.getElementById(opts.hint);
        if (!modeEl || !portEl || !hintEl) return;

        function update() {
            var r = describe(modeEl.value, parseInt(portEl.value || '389', 10), !!opts.rtl);
            hintEl.className = 'alert ' + r.cls + ' py-1 px-2 mb-0 fs-xs';
            hintEl.textContent = r.msg;
        }

        modeEl.addEventListener('change', update);
        portEl.addEventListener('input', update);
        update();
        window.refreshLdapModeHint = update;
    };
})();
