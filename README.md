auto-adds to jellyseerr if no content.\n
added support for strm files will show url from strm file in stremio so not going though jellyfin server.
added basic test to url in strm file if dead will not show in stremio.
added direct media download no transcoding option.


### Installation:


NOTICE: It is recommended to have both your Jellyfin and Jellyseer instances accessilbe over HTTPS by using a Cloudflare tunnel. This is the only way I have tested it so far.

1. Go Open Jellyfin Dashboard > Plugins > Manage Repositories
2. Click "New Repository" and add "Jellio" for the name, and "https://raw.githubusercontent.com/gregb84/jellio-plus/metadata/jellyfin-repo-manifest.json" for the repository url
3. Go back to Plugins, and under "All" find and install Jellio
4. Restart Jellyfin
5. Jellyfin Dashboard > Plugins > Installed > Jellio and then click "Settings"
6. Select which libraries you want to be included in Stremio
7. (Optional) Input your local Jellyseer url (ex. http://192.168.0.105:5055" and your Jellyseer API key. Also include your Public URL for Jellyfin (ex. https://jellyfin.yourserver.com)
8. Click "Save Configuration for Jellyfin"
9. Lastly, click "Install." Copy that link and paste it in your Stremio addons. You're all done!
