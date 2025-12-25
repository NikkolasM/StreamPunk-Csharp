FROM devitllc/mingw-w64@sha256:62e4d38866cea2cc8eeeb03a1530c611f7f7898aa6069a6105f1f822f7b1ea14 # MinGW

WORKDIR /PinThread
COPY workflow.sh .
COPY PinThreadWindows.c .

CMD ["./workflow.sh"]