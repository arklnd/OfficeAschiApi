(function () {
    // Base32 decode
    function base32Decode(encoded) {
        var alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
        encoded = encoded.replace(/[\s=]+/g, '').toUpperCase();
        var bits = '';
        for (var i = 0; i < encoded.length; i++) {
            var val = alphabet.indexOf(encoded[i]);
            if (val === -1) continue;
            bits += val.toString(2).padStart(5, '0');
        }
        var bytes = new Uint8Array(Math.floor(bits.length / 8));
        for (var i = 0; i < bytes.length; i++) {
            bytes[i] = parseInt(bits.substring(i * 8, i * 8 + 8), 2);
        }
        return bytes;
    }

    // Generate 6-digit TOTP from a base32 secret (async, uses Web Crypto)
    async function generateTOTP(base32Secret) {
        var secretBytes = base32Decode(base32Secret);
        var counter = Math.floor(Date.now() / 1000 / 30);

        // 8-byte big-endian counter
        var counterBuf = new ArrayBuffer(8);
        var view = new DataView(counterBuf);
        view.setUint32(0, Math.floor(counter / 0x100000000), false);
        view.setUint32(4, counter >>> 0, false);

        // HMAC-SHA1
        var key = await crypto.subtle.importKey(
            'raw', secretBytes, { name: 'HMAC', hash: 'SHA-1' }, false, ['sign']
        );
        var sig = new Uint8Array(await crypto.subtle.sign('HMAC', key, counterBuf));

        // Dynamic truncation (RFC 6238)
        var offset = sig[sig.length - 1] & 0x0f;
        var code = (
            ((sig[offset] & 0x7f) << 24) |
            ((sig[offset + 1] & 0xff) << 16) |
            ((sig[offset + 2] & 0xff) << 8) |
            (sig[offset + 3] & 0xff)
        ) % 1000000;

        return code.toString().padStart(6, '0');
    }

    // Intercept fetch: if the Authorization header has a base32 secret
    // instead of a 6-digit code, auto-generate the TOTP code.
    var originalFetch = window.fetch;
    window.fetch = async function (url, options) {
        if (options && options.headers) {
            var auth = null;
            if (options.headers instanceof Headers) {
                auth = options.headers.get('Authorization');
            } else if (typeof options.headers === 'object') {
                auth = options.headers['Authorization'] || options.headers['authorization'];
            }

            if (auth && auth.startsWith('SECRET ')) {
                var parts = auth.substring(7).split(':');
                // parts = [type, id, base32Secret]
                if (parts.length === 3) {
                    try {
                        var code = await generateTOTP(parts[2]);
                        var newAuth = 'TOTP ' + parts[0] + ':' + parts[1] + ':' + code;
                        if (options.headers instanceof Headers) {
                            options.headers.set('Authorization', newAuth);
                        } else {
                            options.headers['Authorization'] = newAuth;
                            delete options.headers['authorization'];
                        }
                        console.log('[TOTP] Generated code ' + code + ' from secret');
                    } catch (e) {
                        console.error('[TOTP] Code generation failed:', e);
                    }
                }
            }
        }
        return originalFetch.apply(this, arguments);
    };

    console.log('[TOTP] Swagger TOTP auto-generator loaded. Use SECRET prefix with your base32 secret. The API itself uses TOTP prefix with a 6-digit code.');
})();
