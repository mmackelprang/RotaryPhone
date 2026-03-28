# Google Voice Signaler Protocol

Verified working 2026-03-28 against live GV API.

## Protocol Flow

### Step 1: chooseServer
```
POST https://signaler-pa.clients6.google.com/punctual/v1/chooseServer?key={API_KEY}
Content-Type: application/json+protobuf
Authorization: SAPISIDHASH {hash}
Cookie: {all session cookies including PSIDTS}

Body: [[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]]],null,null,0,0]

Response: ["{gsessionid}",3,null,"{timestamp1}","{timestamp2}"]
```

### Step 2: Bind (create channel with subscriptions)
```
POST https://signaler-pa.clients6.google.com/punctual/multi-watch/channel?VER=8&CVER=22&RID={random}&gsessionid={gsessionid}&key={API_KEY}&t=1
Content-Type: application/x-www-form-urlencoded

Body: count=6&ofs=0
  &req0___data__=[[[1,[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]],null,null,1],null,3]]]
  &req1___data__=[[[2,[null,null,null,[9,5],null,[null,[null,1],[[["3"]]]],null,null,1],null,3]]]
  &req2___data__=[[[3,[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]],null,null,1],null,3]]]
  &req3___data__=[[[4,[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]],null,null,1],null,3]]]
  &req4___data__=[[[5,[null,null,null,[9,5],null,[null,[null,1],[[["2"]]]],null,null,1],null,3]]]
  &req5___data__=[[[6,[null,null,null,[9,5],null,[null,[null,1],[[["3"]]]],null,null,1],null,3]]]

Response: 51\n[[0,["c","{SID}","",8,14,30000]]]
Set-Cookie: S=web-proxy={routing_token}
```

### Step 3: Long-poll (backchannel)
```
GET https://signaler-pa.clients6.google.com/punctual/multi-watch/channel?VER=8&CVER=22&RID=rpc&SID={SID}&AID=0&TYPE=xmlhttp&CI=0&gsessionid={gsessionid}&key={API_KEY}&t=1

Response: Chunked — blocks until event, then returns data and reconnects
```

## Key Findings

1. **`chooseServer` is mandatory** — returns `gsessionid` required for bind and poll
2. **`chooseServer` needs a JSON body** — `[[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]]],null,null,0,0]`
3. **`chooseServer` needs `?key=` in URL** — without it, returns 403
4. **`gsessionid` must be in both bind and poll URLs** — without it, SID is immediately invalidated
5. **`S=web-proxy=...` cookie from bind** is sticky routing — ensures poll hits same backend
6. **PSIDTS cookies are required** — without them, all requests return 401 `SESSION_COOKIE_INVALID`

## Subscription Data Format (needs refinement)

The 6 subscriptions are protobuf-JSON arrays. The inner values `["1"]`, `["3"]`, `["2"]` likely represent different event types (calls, SMS, voicemail). The current format triggers `INVALID_ARGUMENT` for some subscriptions — the exact field structure needs further analysis.

## Required Cookies (12 total)

SID, HSID, SSID, APISID, SAPISID, __Secure-1PSID, __Secure-3PSID,
__Secure-1PAPISID, __Secure-3PAPISID, __Secure-1PSIDTS, __Secure-3PSIDTS,
__Secure-1PSIDCC, __Secure-3PSIDCC
