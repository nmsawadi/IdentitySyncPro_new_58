// Starter templates for common Saudi SMS gateways. These are EDITABLE starting points:
// gateway field names, endpoints, and success tokens change over time, so the admin must
// verify each against the provider's current API docs. Placeholders the engine fills:
//   {username} {password} {apikey} {sender} {recipient} {message}
//   {message_ucs2} — the message as UCS-2 hex, for gateways that flag Unicode with a message
//                    type (e.g. RML's type=2). Use it INSTEAD of {message} on those gateways.
window.SMS_PRESETS = {
    legacy: {
        label: "Adapter / Legacy (fixed JSON)",
        apiUrl: "", httpMethod: "POST", bodyFormat: "Json",
        requestTemplate: "", headersJson: "", successBodyContains: ""
    },
    genericJson: {
        label: "Generic JSON",
        apiUrl: "", httpMethod: "POST", bodyFormat: "Json",
        requestTemplate: '{"userName":"{username}","password":"{password}","senderName":"{sender}","mobileNumber":"{recipient}","message":"{message}"}',
        headersJson: "", successBodyContains: ""
    },
    msegat: {
        label: "Msegat",
        apiUrl: "https://www.msegat.com/gw/sendsms.php", httpMethod: "POST", bodyFormat: "Json",
        requestTemplate: '{"userName":"{username}","apiKey":"{apikey}","numbers":"{recipient}","userSender":"{sender}","msg":"{message}","msgEncoding":"UTF8"}',
        headersJson: "", successBodyContains: '"code":"1"'
    },
    taqnyat: {
        label: "Taqnyat",
        apiUrl: "https://api.taqnyat.sa/v1/messages", httpMethod: "POST", bodyFormat: "Json",
        requestTemplate: '{"recipients":["{recipient}"],"body":"{message}","sender":"{sender}"}',
        headersJson: '{"Authorization":"Bearer {apikey}"}', successBodyContains: "201"
    },
    unifonic: {
        label: "Unifonic",
        apiUrl: "https://el.cloud.unifonic.com/rest/SMS/messages", httpMethod: "POST", bodyFormat: "Form",
        requestTemplate: "AppSid={apikey}&SenderID={sender}&Body={message}&Recipient={recipient}",
        headersJson: "", successBodyContains: '"success":true'
    },
    mobily: {
        label: "Mobily.ws",
        apiUrl: "https://api.mobily.ws/api/msgSend.php", httpMethod: "GET", bodyFormat: "Query",
        requestTemplate: "mobile={username}&password={password}&numbers={recipient}&sender={sender}&msg={message}&applicationType=68",
        headersJson: "", successBodyContains: "1"
    },
    fourJawaly: {
        label: "4Jawaly",
        apiUrl: "https://api-sms.4jawaly.com/api/v1/account/area/sms/send", httpMethod: "POST", bodyFormat: "Json",
        requestTemplate: '{"messages":[{"text":"{message}","numbers":["{recipient}"],"sender":"{sender}"}]}',
        headersJson: '{"Authorization":"Bearer {apikey}","Accept":"application/json"}', successBodyContains: "success"
    },
    rmlConnect: {
        // type=2 marks the message as Unicode, which obliges the UCS-2 hex form — hence
        // {message_ucs2} rather than {message}. Swapping it back corrupts Arabic silently.
        label: "RML Connect (Unicode / Arabic)",
        apiUrl: "https://ksa-api.rmlconnect.net/bulksms/bulksms", httpMethod: "GET", bodyFormat: "Query",
        requestTemplate: "username={username}&password={password}&type=2&dlr=1&destination={recipient}&source={sender}&message={message_ucs2}",
        headersJson: "", successBodyContains: "1701"
    }
};

// Fill the gateway form fields from a preset key. Leaves credential fields untouched.
window.applySmsPreset = function (key) {
    var p = window.SMS_PRESETS[key];
    if (!p) return;
    var set = function (id, val) { var el = document.getElementById(id); if (el != null && val !== undefined) el.value = val; };
    // Only overwrite the URL when the preset provides one (don't wipe a URL the admin typed).
    if (p.apiUrl) set('ApiUrl', p.apiUrl);
    set('HttpMethod', p.httpMethod);
    set('BodyFormat', p.bodyFormat);
    set('RequestTemplate', p.requestTemplate);
    set('HeadersJson', p.headersJson);
    set('SuccessBodyContains', p.successBodyContains);
    if (typeof onSmsBodyFormatChange === 'function') onSmsBodyFormatChange();
};
